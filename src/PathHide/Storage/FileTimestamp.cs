using System;
using System.Globalization;

namespace PathHide.Storage;

/// <summary>
/// The UTC filename stamp — <c>yyyyMMdd-HHmmss-utc</c> — for names the app assigns at runtime. Its one use
/// is <see cref="Services.SessionLog"/>'s fallback log file for a session. Second precision is enough: two
/// sessions starting in the same second append to one file, which is harmless (see the timestamp
/// conventions).
/// </summary>
/// <remarks>
/// A <b>filename</b> stamp is deliberately distinct from the <b>serialized</b> ISO-8601-ms form
/// (<c>2026-07-06T04:05:12.345Z</c>) that stored data values use — a SQLite column, a JSON field. The two
/// never cross: the backup store's <c>written_at_utc</c> is the serialized form, never this stamp. Both
/// live here so neither format string is written out anywhere else.
/// </remarks>
public static class FileTimestamp
{
    private const string FileStampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Filename-safe UTC stamp in the <c>yyyyMMdd-HHmmss-utc</c> form. The instant is converted to
    /// UTC, so the stamp never carries a local offset.</summary>
    public static string FileStamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(FileStampFormat, CultureInfo.InvariantCulture) + "-utc";

    private const string SerializedFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>The serialized UTC form for a stored data value — a SQLite column, a JSON field.</summary>
    public static string SerializedStamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(SerializedFormat, CultureInfo.InvariantCulture);
}
