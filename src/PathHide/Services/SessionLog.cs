using System;
using System.IO;

namespace PathHide.Services;

/// <summary>
/// The plain-text fallback file for one session's entries that its sink could not take (logging
/// conventions, <em>When logging itself fails</em>). One file per session, named by the session's start
/// to the second: <c>yyyymmdd-hhmmss-utc.log</c>. Files from earlier builds keep their millisecond names.
/// </summary>
public static class SessionLog
{
    /// <summary>
    /// The fallback file name for a session that started at <paramref name="sessionStart"/>. The instant
    /// is converted to UTC, so the name does not depend on the local time zone.
    /// </summary>
    public static string FileName(DateTimeOffset sessionStart) =>
        Storage.FileTimestamp.FileStamp(sessionStart) + ".log";

    /// <summary>Appends one line to the session's fallback file, creating the folder and file as needed.</summary>
    public static void Append(string logsDirectory, DateTimeOffset sessionStart, string line)
    {
        Directory.CreateDirectory(logsDirectory);

        // not recorded: an append-only log file, never written through the atomic temp-then-rename path
        // (data-backup conventions).
        File.AppendAllText(Path.Combine(logsDirectory, FileName(sessionStart)), line + "\n");
    }
}
