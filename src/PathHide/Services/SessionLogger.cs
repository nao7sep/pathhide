using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PathHide.Services;

/// <summary>
/// A small hand-rolled structured logger (logging conventions). Each call builds one
/// <see cref="LogEntry"/>: the session, the time, level and message, the caller's free fields, and for
/// errors the full exception (type, message, stack, and cause chain). The entry goes to the sink; one
/// the sink could not take goes to the session's fallback file, and then to the console.
/// </summary>
/// <remarks>
/// It gates <c>debug</c> to developers, and by contract it never throws and never takes the app down
/// because logging failed.
/// </remarks>
public sealed class SessionLogger : IDisposable
{
    // Free fields are serialized to a node tree. Enums are written by name (readable logs); named float
    // literals (NaN, ±∞) are not allowed, so a caller that passes one gets a logError field instead of
    // non-standard JSON.
    private static readonly JsonSerializerOptions NodeOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DateTimeOffset _sessionStart;
    private readonly string _session;
    private readonly ILogSink? _sink;
    private readonly string? _fallbackDirectory;
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>
    /// Creates a logger for the session that started at <paramref name="sessionStart"/>, writing to
    /// <paramref name="sink"/>, which it owns. An entry the sink cannot take, or every entry when there is
    /// no sink, is appended to the session's file under <paramref name="fallbackDirectory"/>, and goes to
    /// the console when that fails too or there is none. When <paramref name="debugEnabled"/> is false,
    /// <c>debug</c> calls are dropped.
    /// </summary>
    public SessionLogger(
        DateTimeOffset sessionStart,
        ILogSink? sink,
        string? fallbackDirectory,
        bool debugEnabled)
    {
        _sessionStart = sessionStart;
        _session = Storage.FileTimestamp.SerializedStamp(sessionStart);
        _sink = sink;
        _fallbackDirectory = fallbackDirectory;
        DebugEnabled = debugEnabled;
    }

    /// <summary>Whether developer-only <c>debug</c> events are written.</summary>
    public bool DebugEnabled { get; }

    public void Debug(string message, object? fields = null)
    {
        if (DebugEnabled)
            Write(Build(LogLevel.Debug, message, null, fields));
    }

    public void Debug(string message, Exception exception, object? fields = null)
    {
        if (DebugEnabled)
            Write(Build(LogLevel.Debug, message, exception, fields));
    }

    public void Info(string message, object? fields = null) =>
        Write(Build(LogLevel.Info, message, null, fields));

    public void Info(string message, Exception exception, object? fields = null) =>
        Write(Build(LogLevel.Info, message, exception, fields));

    public void Warn(string message, object? fields = null) =>
        Write(Build(LogLevel.Warn, message, null, fields));

    public void Warn(string message, Exception exception, object? fields = null) =>
        Write(Build(LogLevel.Warn, message, exception, fields));

    public void Error(string message, object? fields = null) =>
        Write(Build(LogLevel.Error, message, null, fields));

    public void Error(string message, Exception exception, object? fields = null) =>
        Write(Build(LogLevel.Error, message, exception, fields));

    /// <summary>Writes entries another process logged, as it logged them.</summary>
    public void Import(IEnumerable<LogEntry> entries)
    {
        foreach (var entry in entries)
            Write(entry);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            try { _sink?.Dispose(); }
            catch (Exception ex)
            {
                EmitToConsole($"[logger] closing the sink failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void Write(LogEntry entry)
    {
        // Under the lock, so entries reach the sink in the order they were stamped.
        lock (_gate)
        {
            Exception? sinkError = null;
            if (_sink is not null && !_disposed)
            {
                try
                {
                    _sink.Write(entry);
                    return;
                }
                catch (Exception ex)
                {
                    sinkError = ex;
                }
            }

            if (_fallbackDirectory is not null)
            {
                try
                {
                    SessionLog.Append(_fallbackDirectory, _sessionStart, entry.ToLine());
                    if (sinkError is not null)
                        SessionLog.Append(_fallbackDirectory, _sessionStart,
                            Build(LogLevel.Error, "logger: the sink did not take the entry above", sinkError, null).ToLine());
                    return;
                }
                catch (Exception fallbackError)
                {
                    EmitToConsole(entry.ToLine());
                    EmitToConsole($"[logger] fallback file write failed: {fallbackError.GetType().Name}: {fallbackError.Message}");
                }
            }
            else
            {
                EmitToConsole(entry.ToLine());
            }

            if (sinkError is not null)
                EmitToConsole($"[logger] sink write failed: {sinkError.GetType().Name}: {sinkError.Message}");
        }
    }

    private LogEntry Build(LogLevel level, string message, Exception? exception, object? fields)
    {
        JsonObject? fieldNode = null;
        if (fields is not null)
        {
            try
            {
                // Free fields are named values by contract: a non-object (someone passed a bare string or
                // number) has no field names and is ignored.
                fieldNode = JsonSerializer.SerializeToNode(fields, fields.GetType(), NodeOptions) as JsonObject;
            }
            catch (Exception serializeError)
            {
                // Serialization must never take the app down; the event is kept with the reason its fields
                // are missing.
                fieldNode = new JsonObject { ["logError"] = serializeError.GetType().Name };
            }
        }

        return new LogEntry(
            _session,
            Storage.FileTimestamp.SerializedStamp(DateTimeOffset.UtcNow),
            LevelName(level),
            message,
            fieldNode,
            exception is null ? null : BuildErrorNode(exception));
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

    private static void EmitToConsole(string text)
    {
        // The sink and the fallback file are unavailable (disk full, permissions) or there are none.
        // If even the console is gone, by contract we still never throw.
        try { Console.Error.WriteLine(text); }
        catch { /* nothing left to surface this to */ }
    }
}
