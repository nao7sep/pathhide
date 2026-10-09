using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PathHide.Services;

/// <summary>
/// A small hand-rolled structured logger (logging conventions). Each call builds one
/// <see cref="LogEntry"/>: the session, the time, level and message, the caller's free fields, and for
/// errors the full exception (type, message, stack, and cause chain). The entry goes to the sink; one
/// the sink could not take goes to the session's fallback file, and then to the console. A logger that
/// writes in the background stamps each entry on the caller's thread and writes it from its own, so a
/// slow or locked sink never holds the caller.
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

    // PLAYBOOK, Own the work in flight: how long closing waits for the queued entries. After the window
    // has gone, an ordinary quit can afford it; when the OS is ending the session the quit has already
    // spent its bound, so the drain gets only a moment before what is left goes to the fallback file.
    internal static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan SessionEndDrainBound = TimeSpan.FromSeconds(1);

    // How many entries may wait for a stalled sink (logging-conventions: no unbounded pending records).
    // A session writes tens of entries and a large apply with failures a few hundred, so this only fills
    // while the sink is stalled; past it, entries are counted and dropped rather than written from the
    // caller's thread, and the writer records how many once it catches up.
    internal const int QueueCapacity = 1000;

    private readonly DateTimeOffset _sessionStart;
    private readonly string _session;
    private readonly ILogSink? _sink;
    private readonly string? _fallbackDirectory;
    private int _dropped;
    private readonly BlockingCollection<LogEntry>? _queue;
    private readonly Thread? _writer;
    private readonly object _sinkGate = new();
    private readonly object _fallbackGate = new();
    private readonly object _closeGate = new();
    private readonly Action? _stored;
    private bool _sinkClosed;
    private bool _closed;

    /// <summary>
    /// Creates a logger for the session that started at <paramref name="sessionStart"/>, writing to
    /// <paramref name="sink"/>, which it owns. An entry the sink cannot take, or every entry when there is
    /// no sink, is appended to the session's file under <paramref name="fallbackDirectory"/>, and goes to
    /// the console when that fails too or there is none. When <paramref name="debugEnabled"/> is false,
    /// <c>debug</c> calls are dropped. When <paramref name="writeInBackground"/> is true, entries are
    /// written in order by the logger's own thread, and closing waits a bounded time for them.
    /// <paramref name="stored"/> is called, on the thread that wrote it, after each entry the sink took;
    /// an entry that went to the fallback file calls nothing.
    /// </summary>
    public SessionLogger(
        DateTimeOffset sessionStart,
        ILogSink? sink,
        string? fallbackDirectory,
        bool debugEnabled,
        bool writeInBackground,
        Action? stored = null)
    {
        _sessionStart = sessionStart;
        _session = Storage.FileTimestamp.SerializedStamp(sessionStart);
        _sink = sink;
        _fallbackDirectory = fallbackDirectory;
        _stored = stored;
        DebugEnabled = debugEnabled;

        if (writeInBackground)
        {
            _queue = new BlockingCollection<LogEntry>(QueueCapacity);
            // A background thread, so a sink that never returns cannot keep the process alive.
            _writer = new Thread(WriteQueued) { IsBackground = true, Name = "PathHide log writer" };
            _writer.Start();
        }
    }

    /// <summary>Whether developer-only <c>debug</c> events are written.</summary>
    public bool DebugEnabled { get; }

    /// <summary>This launch's session, as every entry it builds carries.</summary>
    public string Session => _session;

    public void Debug(string message, object? fields = null)
    {
        if (DebugEnabled)
            Submit(Build(LogLevel.Debug, message, null, fields));
    }

    public void Debug(string message, Exception exception, object? fields = null)
    {
        if (DebugEnabled)
            Submit(Build(LogLevel.Debug, message, exception, fields));
    }

    public void Info(string message, object? fields = null) =>
        Submit(Build(LogLevel.Info, message, null, fields));

    public void Info(string message, Exception exception, object? fields = null) =>
        Submit(Build(LogLevel.Info, message, exception, fields));

    public void Warn(string message, object? fields = null) =>
        Submit(Build(LogLevel.Warn, message, null, fields));

    public void Warn(string message, Exception exception, object? fields = null) =>
        Submit(Build(LogLevel.Warn, message, exception, fields));

    public void Error(string message, object? fields = null) =>
        Submit(Build(LogLevel.Error, message, null, fields));

    public void Error(string message, Exception exception, object? fields = null) =>
        Submit(Build(LogLevel.Error, message, exception, fields));

    internal Task ReportStartupAsync(string message, Exception? error, bool warning = false)
    {
        var entry = Build(warning ? LogLevel.Warn : error is null ? LogLevel.Info : LogLevel.Error, message, error, null);
        return Task.Run(() => Submit(entry));
    }

    /// <summary>Writes entries another process logged, as it logged them.</summary>
    public void Import(IEnumerable<LogEntry> entries)
    {
        foreach (var entry in entries)
            Submit(entry);
    }

    /// <summary>Writes what is queued, within the bound, and closes the sink.</summary>
    public void Dispose() => Close(DrainBound);

    /// <summary>
    /// Closes the logger, waiting at most <paramref name="drainBound"/> for the queued entries. When the
    /// sink is still busy after that, the entries left in the queue go to the fallback file, and the sink
    /// is left to the writer thread rather than closed under it.
    /// </summary>
    internal void Close(TimeSpan drainBound)
    {
        lock (_closeGate)
        {
            if (_closed)
                return;
            _closed = true;
        }

        if (_queue is not null)
        {
            _queue.CompleteAdding();
            if (!_writer!.Join(drainBound))
            {
                // Logging remains optional even when the fallback volume or sink close stalls.
                var fallback = new Thread(() =>
                {
                    var left = 0;
                    while (_queue.TryTake(out var entry))
                    {
                        WriteFallback(entry, sinkError: null);
                        left++;
                    }
                    WriteFallback(Build(LogLevel.Error, "logger: the sink did not finish before closing",
                        null, new { waitedMs = (long)drainBound.TotalMilliseconds, left }), sinkError: null);
                    if (TakeDroppedNote() is { } note)
                        WriteFallback(note, sinkError: null);
                    _writer.Join();
                    _queue.Dispose();
                }) { IsBackground = true, Name = "PathHide log fallback drain" };
                fallback.Start();
            }
            else
                _queue.Dispose();
            return;
        }

        var close = new Thread(CloseSink) { IsBackground = true, Name = "PathHide log close" };
        close.Start();
        close.Join(drainBound);
    }

    private void CloseSink()
    {
        lock (_sinkGate)
        {
            _sinkClosed = true;
            try { _sink?.Dispose(); }
            catch (Exception ex)
            {
                EmitToConsole($"[logger] closing the sink failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>A warning naming how many entries the full queue dropped since the last one, or null.</summary>
    private LogEntry? TakeDroppedNote()
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        return dropped == 0
            ? null
            : Build(LogLevel.Warn, "logger: entries dropped while the sink was busy", null, new { dropped });
    }

    private void Submit(LogEntry entry)
    {
        if (_queue is null)
        {
            Write(entry);
            return;
        }

        try
        {
            if (!_queue.TryAdd(entry))
                Interlocked.Increment(ref _dropped);
        }
        catch (InvalidOperationException)
        {
            // Closed: the sink is closing or closed, so the entry goes straight to the fallback.
            WriteFallback(entry, sinkError: null);
        }
    }

    private void WriteQueued()
    {
        try
        {
            foreach (var entry in _queue!.GetConsumingEnumerable())
            {
                Write(entry);
                if (TakeDroppedNote() is { } note)
                    Write(note);
            }
        }
        finally { CloseSink(); }
    }

    private void Write(LogEntry entry)
    {
        Exception? sinkError = null;
        var written = false;
        lock (_sinkGate)
        {
            if (_sink is not null && !_sinkClosed)
            {
                try
                {
                    _sink.Write(entry);
                    written = true;
                }
                catch (Exception ex)
                {
                    sinkError = ex;
                }
            }
        }

        if (written)
        {
            NotifyStored();
            return;
        }

        WriteFallback(entry, sinkError);
    }

    // Outside the sink's lock, so a listener that logs cannot deadlock the writer.
    private void NotifyStored()
    {
        if (_stored is null)
            return;

        try
        {
            _stored();
        }
        catch (Exception ex)
        {
            // A listener's failure is reported, never let through to the writer thread, which would end
            // the process.
            EmitToConsole($"[logger] a stored-entry listener failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void WriteFallback(LogEntry entry, Exception? sinkError)
    {
        lock (_fallbackGate)
        {
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
