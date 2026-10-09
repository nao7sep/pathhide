using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PathHide.Services;

/// <summary>
/// Process-wide logging facade. <see cref="Start"/> hands it this session's sink, installs last-resort
/// crash hooks, and routes every subsequent call to a <see cref="SessionLogger"/>. Before
/// <see cref="Start"/> calls go to the console rather than being lost.
/// </summary>
/// <remarks>
/// The facade is a thin pass-through; the testable behavior lives in <see cref="SessionLogger"/>. The
/// free-field overloads mirror the logger: <c>Level(message, fields)</c> for a plain event and
/// <c>Level(message, exception, fields)</c> when an exception is in play.
/// </remarks>
public static class Log
{
    private static readonly object Gate = new();

    // One process launch is one session, named by when it began.
    private static readonly DateTimeOffset SessionStart = DateTimeOffset.UtcNow;

    // Always non-null: a console-backed logger until Start swaps in the session's, and again after
    // Shutdown, so an event is never silently dropped.
    private static volatile SessionLogger _logger = CreateConsoleLogger();
    private static bool _started;
    private static bool _hooksInstalled;
    private static volatile bool _sessionEnding;

    /// <summary>Whether developer-only <c>debug</c> events are being written.</summary>
    public static bool DebugEnabled => _logger.DebugEnabled;

    /// <summary>This launch's session, as every entry it logs carries.</summary>
    public static string Session => _logger.Session;

    /// <summary>
    /// Raised after each entry this session's sink stored, on the logger's writer thread, so the records
    /// window can read it. An entry that went to the fallback file or the console raises nothing.
    /// </summary>
    public static event Action? RecordStored;

    // Optional early diagnostics cannot hold required startup or its failure presentation.
    internal static Task ReportStartup(string message, Exception? error = null, SessionLogger? logger = null,
        bool warning = false)
    {
        var target = logger ?? _logger;
        return target.ReportStartupAsync(message, error, warning);
    }

    /// <summary>
    /// Begins logging this session to <paramref name="sink"/>, which the log then owns: the records
    /// database in the app, the results file in the elevated child. Entries are written off the
    /// caller's thread, so a slow or locked sink never holds the interface. An entry the sink cannot
    /// take, or every entry when <paramref name="sink"/> is null, goes to the session's fallback file
    /// under <paramref name="logsDirectory"/>. A second call is ignored and closes the sink it was given.
    /// </summary>
    public static void Start(ILogSink? sink, string logsDirectory)
    {
        SessionLogger previous;
        lock (Gate)
        {
            if (_started)
                previous = new SessionLogger(SessionStart, sink, null, IsDebugEnabled(), writeInBackground: false);
            else
            {
                _started = true;
                _sessionEnding = false;
                InstallCrashHooks();
                previous = _logger;
                _logger = new SessionLogger(SessionStart, sink, logsDirectory, IsDebugEnabled(), writeInBackground: true,
                    stored: () => RecordStored?.Invoke());
            }
        }
        previous.Dispose();
    }

    /// <summary>
    /// Records that the operating system is ending the session, so <see cref="Shutdown"/> gives the
    /// queued entries only <see cref="SessionLogger.SessionEndDrainBound"/>: logging must not hold up a
    /// logout or restart past the quit's own bound (logging-conventions).
    /// </summary>
    public static void EndingSession() => _sessionEnding = true;

    /// <summary>
    /// Writes the queued entries, within a bound, and closes the session's sink. Idempotent. Late events
    /// that arrive after shutdown fall back to the console rather than being lost.
    /// </summary>
    public static void Shutdown()
    {
        SessionLogger previous;
        lock (Gate)
        {
            if (!_started)
                return;
            _started = false;

            previous = _logger;
            _logger = CreateConsoleLogger();
        }
        previous.Close(_sessionEnding ? SessionLogger.SessionEndDrainBound : SessionLogger.DrainBound);
    }

    /// <summary>Writes entries another process logged and handed back, as it logged them.</summary>
    public static void Import(IEnumerable<LogEntry> entries) => _logger.Import(entries);

    public static void Debug(string message, object? fields = null) => _logger.Debug(message, fields);
    public static void Debug(string message, Exception exception, object? fields = null) => _logger.Debug(message, exception, fields);
    public static void Info(string message, object? fields = null) => _logger.Info(message, fields);
    public static void Info(string message, Exception exception, object? fields = null) => _logger.Info(message, exception, fields);
    public static void Warn(string message, object? fields = null) => _logger.Warn(message, fields);
    public static void Warn(string message, Exception exception, object? fields = null) => _logger.Warn(message, exception, fields);
    public static void Error(string message, object? fields = null) => _logger.Error(message, fields);
    public static void Error(string message, Exception exception, object? fields = null) => _logger.Error(message, exception, fields);

    private static SessionLogger CreateConsoleLogger() =>
        new(SessionStart, new TextWriterLogSink(Console.Error, leaveOpen: true), fallbackDirectory: null, IsDebugEnabled(),
            writeInBackground: false);

    // Debug is developer-only: on in a development (DEBUG) build, otherwise only when
    // PATHHIDE_DEBUG=1 is set. In a release build with no such variable it is off, so
    // the per-item firehose never reaches an end-user disk.
    private static bool IsDebugEnabled()
    {
#if DEBUG
        return true;
#else
        return Environment.GetEnvironmentVariable("PATHHIDE_DEBUG") == "1";
#endif
    }

    private static void InstallCrashHooks()
    {
        if (_hooksInstalled)
            return;
        _hooksInstalled = true;

        // Last-resort nets so the final lines before a crash are written.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Error("unhandled exception; process terminating", e.ExceptionObject as Exception ?? new Exception("non-Exception throw"),
                new { terminating = e.IsTerminating });
            if (e.IsTerminating)
                Shutdown();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Logged, not observed: in modern .NET an unobserved task exception is
            // already non-fatal, and changing that policy is not logging's job.
            Error("unobserved task exception", e.Exception);
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }
}
