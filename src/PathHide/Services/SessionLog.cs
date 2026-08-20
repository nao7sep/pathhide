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
    /// <summary>
    /// The log file name for a launch at <paramref name="timestamp"/>. The instant
    /// is converted to UTC, so the name does not depend on the local time zone.
    /// </summary>
    public static string FileName(DateTimeOffset timestamp)
    {
        // Through FileTimestamp, which is what its own doc says this file does —
        // it kept a private copy of the same format string, so editing one left
        // log filenames on the old form while the doc pointed at the other.
        return Storage.FileTimestamp.FileStamp(timestamp) + ".log";
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

        // not recorded: per-session logs under ~/.pathhide/logs/ are excluded by construction — they are
        // opened as a streamed, append-style file, never written through the atomic temp-then-rename path,
        // so they never reach the backup hook (data-backup conventions). They are also recreatable and
        // noisy, not durable user text.
        var path = Path.Combine(logsDirectory, FileName(timestamp));
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream) { AutoFlush = false };
    }
}
