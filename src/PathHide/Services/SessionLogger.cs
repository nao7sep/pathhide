using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PathHide.Services;

/// <summary>
/// A small hand-rolled JSON-Lines logger — the reference implementation for the
/// project's logging conventions. Each call serializes one structured event to a
/// single line: an envelope (<c>time</c> / <c>level</c> / <c>message</c>) plus the
/// caller's free fields and, for errors, the full exception (type, message, stack,
/// and cause chain).
/// </summary>
/// <remarks>
/// The logger owns a <see cref="TextWriter"/> and writes under a lock. It gates
/// <c>debug</c> to developers, runs the mandatory <see cref="LogRedactor"/> over the
/// free fields, flushes <c>warn</c> / <c>error</c> / <c>debug</c> immediately (while
/// <c>info</c> may stay buffered), and degrades to the console if the writer ever
/// fails. By contract it never throws and never takes the app down because logging
/// failed.
/// </remarks>
public sealed class SessionLogger : IDisposable
{
    // Free fields are serialized to a node tree so the redactor can walk them.
    // Enums are written by name (readable logs); named float literals (NaN, ±∞) are
    // intentionally NOT allowed here — if a caller passes one, serialization fails
    // and Write falls back to a minimal hand-built line rather than emitting
    // non-standard JSON.
    private static readonly JsonSerializerOptions NodeOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // One physical line per event. Relaxed escaping keeps non-ASCII (e.g. Japanese
    // path components) readable; newlines inside string values are still escaped, so
    // one event remains one line.
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HashSet<string> ReservedKeys =
        new(StringComparer.OrdinalIgnoreCase) { "time", "level", "message", "error" };

    private readonly TextWriter _writer;
    private readonly bool _leaveOpen;
    private readonly IReadOnlySet<string> _deniedKeys;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>
    /// Creates a logger over <paramref name="writer"/>. When
    /// <paramref name="debugEnabled"/> is false, <see cref="Debug(string, object?)"/>
    /// calls are dropped. <paramref name="deniedKeys"/> must use a case-insensitive
    /// comparer. Set <paramref name="leaveOpen"/> for shared writers (the console)
    /// that this logger must not close on <see cref="Dispose"/>.
    /// </summary>
    public SessionLogger(
        TextWriter writer,
        bool debugEnabled,
        IReadOnlySet<string> deniedKeys,
        bool leaveOpen = false)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        DebugEnabled = debugEnabled;
        _deniedKeys = deniedKeys ?? throw new ArgumentNullException(nameof(deniedKeys));
        _leaveOpen = leaveOpen;
    }

    /// <summary>Whether developer-only <c>debug</c> events are written.</summary>
    public bool DebugEnabled { get; }

    public void Debug(string message, object? fields = null)
    {
        if (DebugEnabled)
            Write(LogLevel.Debug, message, null, fields);
    }

    public void Debug(string message, Exception exception, object? fields = null)
    {
        if (DebugEnabled)
            Write(LogLevel.Debug, message, exception, fields);
    }

    public void Info(string message, object? fields = null) =>
        Write(LogLevel.Info, message, null, fields);

    public void Info(string message, Exception exception, object? fields = null) =>
        Write(LogLevel.Info, message, exception, fields);

    public void Warn(string message, object? fields = null) =>
        Write(LogLevel.Warn, message, null, fields);

    public void Warn(string message, Exception exception, object? fields = null) =>
        Write(LogLevel.Warn, message, exception, fields);

    public void Error(string message, object? fields = null) =>
        Write(LogLevel.Error, message, null, fields);

    public void Error(string message, Exception exception, object? fields = null) =>
        Write(LogLevel.Error, message, exception, fields);

    /// <summary>Flushes buffered lines to the underlying writer. Best-effort.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            try { _writer.Flush(); }
            catch (Exception ex)
            {
                EmitToConsole($"[logger] flush failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            try { _writer.Flush(); }
            catch (Exception ex)
            {
                EmitToConsole($"[logger] final flush failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (!_leaveOpen)
            {
                try { _writer.Dispose(); }
                catch (Exception ex)
                {
                    EmitToConsole($"[logger] dispose failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private void Write(LogLevel level, string message, Exception? exception, object? fields)
    {
        // Serialize under the same lock that writes, so a line's `time` and its
        // physical position in the file agree: two threads cannot stamp their
        // timestamps in one order and then write in the opposite order. Logging here
        // is low-volume (human-cadence actions and IO boundaries), so holding the lock
        // across serialization costs nothing and removes a class of ordering surprises.
        lock (_gate)
        {
            string line;
            try
            {
                line = Serialize(level, message, exception, fields);
            }
            catch (Exception serializeError)
            {
                // Serialization must never take the app down. Emit a minimal line so
                // the event is not lost, and record that serialization failed.
                line = FallbackLine(level, message, serializeError);
            }

            if (_disposed)
            {
                // Disposed mid-call — e.g. shutdown raced an in-flight log from another
                // thread. Surface it on the console rather than dropping it silently.
                EmitToConsole(line);
                return;
            }

            try
            {
                _writer.WriteLine(line);
                // info may stay buffered for efficiency; everything else is wanted
                // on disk immediately (you are actively debugging when you read it).
                if (level != LogLevel.Info)
                    _writer.Flush();
            }
            catch (Exception writeError)
            {
                EmitToConsole(line);
                EmitToConsole($"[logger] file write failed: {writeError.GetType().Name}: {writeError.Message}");
            }
        }
    }

    private string Serialize(LogLevel level, string message, Exception? exception, object? fields)
    {
        var root = new JsonObject
        {
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["level"] = LevelName(level),
            ["message"] = message,
        };

        if (fields is not null)
        {
            var node = JsonSerializer.SerializeToNode(fields, fields.GetType(), NodeOptions);

            // Free fields are named values by contract: a non-object (someone passed a
            // bare string or number) has no field names and is ignored.
            if (node is JsonObject fieldObj)
            {
                LogRedactor.Redact(fieldObj, _deniedKeys);
                MergeFields(root, fieldObj);
            }
        }

        if (exception is not null)
            root["error"] = BuildErrorNode(exception);

        return root.ToJsonString(LineOptions);
    }

    private static void MergeFields(JsonObject root, JsonObject fields)
    {
        // Reparent each free field onto the envelope. A JsonNode can have only one
        // parent, so detach before assigning. Reserved envelope keys win — a stray
        // free field named "message" can never shadow the real message.
        foreach (var name in new List<string>(fields.Select(pair => pair.Key)))
        {
            if (ReservedKeys.Contains(name))
                continue;

            var value = fields[name];
            fields.Remove(name);
            root[name] = value;
        }
    }

    private static JsonObject BuildErrorNode(Exception exception)
    {
        var node = new JsonObject
        {
            ["type"] = exception.GetType().FullName,
            ["message"] = exception.Message,
            ["stack"] = exception.StackTrace,
        };

        // Full fidelity: walk the cause chain. AggregateException can wrap several
        // causes at once, so capture all of them; otherwise follow InnerException.
        if (exception is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
        {
            var causes = new JsonArray();
            foreach (var inner in aggregate.InnerExceptions)
                causes.Add(BuildErrorNode(inner));
            node["causes"] = causes;
        }
        else if (exception.InnerException is not null)
        {
            node["cause"] = BuildErrorNode(exception.InnerException);
        }

        return node;
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "debug",
        LogLevel.Info => "info",
        LogLevel.Warn => "warn",
        LogLevel.Error => "error",
        _ => "info",
    };

    private static string FallbackLine(LogLevel level, string message, Exception serializeError)
    {
        // Last resort: even serialization failed. Build a valid line by hand from the
        // few values we fully control, escaping the message defensively.
        var safeMessage = message
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
        var time = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return $"{{\"time\":\"{time}\",\"level\":\"{LevelName(level)}\",\"message\":\"{safeMessage}\","
             + $"\"logError\":\"{serializeError.GetType().Name}\"}}";
    }

    private static void EmitToConsole(string text)
    {
        // The file is unavailable (disk full, permissions) or already closed. Degrade
        // to the console using only what is already available — no new dependencies —
        // and keep running. If even the console is gone, by contract we still never throw.
        try { Console.Error.WriteLine(text); }
        catch { /* nothing left to surface this to */ }
    }
}
