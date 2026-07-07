using System;
using System.Globalization;

namespace PathHide.Storage;

/// <summary>
/// The machine-paced UTC filename stamp — <c>yyyyMMdd-HHmmss-fff-utc</c> — for names the app assigns at
/// runtime as part of its own operation. The one current use is <see cref="JsonStore{T}.QuarantinePath"/>'s
/// <c>&lt;stem&gt;-&lt;stamp&gt;.invalid</c> quarantine name; the same form is what <see cref="Services.SessionLog"/>
/// uses for its per-launch log file, so the fleet has one machine-paced filename formatter rather than
/// several (see the timestamp conventions).
/// </summary>
/// <remarks>
/// This is a <b>filename</b> stamp, deliberately distinct from the <b>serialized</b> ISO-8601-ms form
/// (<c>2026-07-06T04:05:12.345Z</c>) that stored data values use — a SQLite column, a JSON field. The two
/// never cross: the backup store's <c>written_at_utc</c> is the serialized form, never this stamp.
/// </remarks>
public static class FileTimestamp
{
    private const string FileStampFormat = "yyyyMMdd-HHmmss-fff";

    /// <summary>Filename-safe UTC stamp in the <c>yyyyMMdd-HHmmss-fff-utc</c> convention (the millisecond,
    /// machine-paced form). The instant is converted to UTC, so the stamp never carries a local offset.</summary>
    public static string FileStamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(FileStampFormat, CultureInfo.InvariantCulture) + "-utc";
}
