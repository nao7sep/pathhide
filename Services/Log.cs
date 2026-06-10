using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace PathHide.Services;

/// <summary>
/// Process-wide logging facade. <see cref="Start"/> opens the one per-launch log
/// file under the app's logs directory, installs last-resort crash hooks, and routes
/// every subsequent call to a <see cref="SessionLogger"/>. Before <see cref="Start"/>
/// — or if the file cannot be opened — calls degrade to the console rather than being
/// lost.
/// </summary>
/// <remarks>
/// The facade is a thin pass-through; the testable behavior lives in
/// <see cref="SessionLogger"/> and <see cref="LogRedactor"/>. The free-field overloads
/// mirror the logger: <c>Level(message, fields)</c> for a plain event and
/// <c>Level(message, exception, fields)</c> when an exception is in play.
/// </remarks>
public static class Log
{
    // The obvious secrets, per the conventions' seed set. PathHide logs none of these
    // today — it deals in file paths, not credentials — but the redactor is a
    // mandatory backstop, and each app extends its own set as needed.
    private static readonly IReadOnlySet<string> DeniedKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "apiKey", "authorization", "token", "password", "secret",
        };

    private static readonly object Gate = new();

    // Always non-null: a console-backed logger until Start swaps in the file-backed
    // one, and again after Shutdown, so an event is never silently dropped.
    private static volatile SessionLogger _logger = CreateConsoleLogger();
    private static bool _started;
    private static bool _hooksInstalled;

    /// <summary>Whether developer-only <c>debug</c> events are being written.</summary>
    public static bool DebugEnabled => _logger.DebugEnabled;

    /// <summary>
    /// Opens the per-session log file for this process launch and begins logging. A
    /// second call is ignored — one session is one file. If the file cannot be opened
    /// the app still runs, logging to the console.
    /// </summary>
    public static void Start(string logsDirectory)
    {
        lock (Gate)
        {
            if (_started)
                return;
            _started = true;

            InstallCrashHooks();

            SessionLogger fileLogger;
            try
            {
                Directory.CreateDirectory(logsDirectory);
                var path = Path.Combine(logsDirectory, SessionLog.FileName(DateTime.UtcNow));

                // Append so a same-named file is never truncated; FileShare.Read keeps
                // it readable while open (the "Open Log File" reveal, external tail).
                // Two launches in the same UTC second are the only way two sessions
                // share a name — not special-cased here, as in most single-user desktop
                // apps; a second concurrent instance is already racing on paths.json.
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var writer = new StreamWriter(stream) { AutoFlush = false };
                fileLogger = new SessionLogger(writer, IsDebugEnabled(), DeniedKeys);
            }
            catch (Exception ex)
            {
                // Best-effort: keep the console logger and report why. Launching must
                // never fail because logging could not open its file.
                _logger.Error("logger: could not open session file; logging to console", ex, new { logsDirectory });
                return;
            }

            var previous = _logger;
            _logger = fileLogger;
            previous.Dispose(); // console logger: leaveOpen, so this only flushes
        }
    }

    /// <summary>
    /// Flushes and closes the session file. Idempotent. Late events that arrive after
    /// shutdown fall back to the console rather than being lost.
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!_started)
                return;
            _started = false;

            var previous = _logger;
            _logger = CreateConsoleLogger();
            previous.Dispose();
        }
    }

    /// <summary>Flushes buffered lines without closing the file.</summary>
    public static void Flush() => _logger.Flush();

    public static void Debug(string message, object? fields = null) => _logger.Debug(message, fields);
    public static void Debug(string message, Exception exception, object? fields = null) => _logger.Debug(message, exception, fields);
    public static void Info(string message, object? fields = null) => _logger.Info(message, fields);
    public static void Info(string message, Exception exception, object? fields = null) => _logger.Info(message, exception, fields);
    public static void Warn(string message, object? fields = null) => _logger.Warn(message, fields);
    public static void Warn(string message, Exception exception, object? fields = null) => _logger.Warn(message, exception, fields);
    public static void Error(string message, object? fields = null) => _logger.Error(message, fields);
    public static void Error(string message, Exception exception, object? fields = null) => _logger.Error(message, exception, fields);

    private static SessionLogger CreateConsoleLogger() =>
        new(Console.Error, IsDebugEnabled(), DeniedKeys, leaveOpen: true);

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

        // Last-resort nets so the final lines before a crash reach disk.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Error("unhandled exception; process terminating", e.ExceptionObject as Exception ?? new Exception("non-Exception throw"),
                new { terminating = e.IsTerminating });
            Flush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Logged, not observed: in modern .NET an unobserved task exception is
            // already non-fatal, and changing that policy is not logging's job.
            Error("unobserved task exception", e.Exception);
            Flush();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }
}
