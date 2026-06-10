using System;
using System.Globalization;

namespace PathHide.Services;

/// <summary>
/// Naming for per-launch log files: one file per process launch, named with a
/// UTC timestamp (<c>yyyymmdd-hhmmss-utc.log</c>) so files sort chronologically
/// and are unambiguous across timezones, per the project's timestamp-filename
/// convention.
/// </summary>
public static class SessionLog
{
    /// <summary>
    /// The log file name for a launch at <paramref name="timestamp"/>. The instant
    /// is converted to UTC, so the name does not depend on the local time zone.
    /// </summary>
    public static string FileName(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-utc.log";
}
