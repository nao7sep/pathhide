using System;
using System.Globalization;
using System.IO;

namespace PathHide.Services;

/// <summary>
/// Naming and creation for per-launch log files: one fresh file per process
/// launch, named strictly <c>yyyymmdd-hhmmss-fff-utc.log</c> — the UTC session-start
/// timestamp (with milliseconds) and nothing else, no uniqueness suffix. Two launches
/// in the same UTC millisecond collide on the name; the exclusive create then fails
/// and the caller (see <c>Log.Start</c>) degrades to console logging, rather than the
/// collision being engineered around.
/// </summary>
public static class SessionLog
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss-fff";

    /// <summary>
    /// The log file name for a launch at <paramref name="timestamp"/>. The instant
    /// is converted to UTC, so the name does not depend on the local time zone.
    /// </summary>
    public static string FileName(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return utc.ToString(TimestampFormat, CultureInfo.InvariantCulture) + "-utc.log";
    }

    /// <summary>
    /// Opens the fresh log file for a real process launch.
    /// </summary>
    public static StreamWriter OpenWriter(string logsDirectory) =>
        OpenWriter(logsDirectory, DateTimeOffset.UtcNow);

    /// <summary>
    /// Opens a fresh log file for the specified launch instant. This overload is
    /// deterministic for tests and tools that need to assert the physical file name.
    /// </summary>
    public static StreamWriter OpenWriter(string logsDirectory, DateTimeOffset timestamp)
    {
        Directory.CreateDirectory(logsDirectory);

        var path = Path.Combine(logsDirectory, FileName(timestamp));
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream) { AutoFlush = false };
    }
}
