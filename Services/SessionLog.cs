using System;
using System.Globalization;
using System.IO;

namespace PathHide.Services;

/// <summary>
/// Naming and creation for per-launch log files: one fresh file per process
/// launch. The UTC timestamp keeps files sorted chronologically; the session id
/// makes the launch identity explicit, so rapid restarts never share a file.
/// </summary>
public static class SessionLog
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss-fff";

    /// <summary>
    /// The log file name for a launch at <paramref name="timestamp"/> with the
    /// supplied per-session identifier. The instant is converted to UTC, so the
    /// name does not depend on the local time zone.
    /// </summary>
    public static string FileName(DateTimeOffset timestamp, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id must be non-empty.", nameof(sessionId));

        var utc = timestamp.ToUniversalTime();
        return utc.ToString(TimestampFormat, CultureInfo.InvariantCulture)
            + "-utc-"
            + sessionId.ToLowerInvariant()
            + ".log";
    }

    /// <summary>
    /// Opens the fresh log file for a real process launch.
    /// </summary>
    public static StreamWriter OpenWriter(string logsDirectory) =>
        OpenWriter(logsDirectory, DateTimeOffset.UtcNow, NewSessionId());

    /// <summary>
    /// Opens a fresh log file for the specified launch identity. This overload is
    /// deterministic for tests and tools that need to assert the physical file name.
    /// </summary>
    public static StreamWriter OpenWriter(string logsDirectory, DateTimeOffset timestamp, string sessionId)
    {
        Directory.CreateDirectory(logsDirectory);

        var path = Path.Combine(logsDirectory, FileName(timestamp, sessionId));
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream) { AutoFlush = false };
    }

    private static string NewSessionId() =>
        Guid.NewGuid().ToString("N");
}
