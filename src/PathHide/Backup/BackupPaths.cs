using System;
using System.Globalization;
using System.IO;

namespace PathHide.Backup;

/// <summary>
/// Resolves the locations the data-backup feature uses under the app's home root
/// (<c>~/.pathhide/</c>, honoring <c>PATHHIDE_HOME</c>): the <c>backups/</c> directory that holds the
/// archives and the change ledger <c>backups/index.json</c>. The home root itself is what the backup
/// mirrors. Kept free of Avalonia/UI dependencies so the pure backup logic stays unit-testable.
/// </summary>
/// <remarks>
/// The root is resolved once at construction (via the supplied home directory), so a run sees a single,
/// consistent tree even if <c>PATHHIDE_HOME</c> were to change mid-process. In production the home is
/// <see cref="Storage.StorageRoot.Directory"/>; tests pass a throwaway directory directly.
/// </remarks>
public sealed class BackupPaths
{
    public BackupPaths(string homeRoot) => HomeRoot = homeRoot;

    /// <summary>The app's home root — the tree the backup mirrors (<c>~/.pathhide/</c>).</summary>
    public string HomeRoot { get; }

    /// <summary>Where the feature keeps its archives and index (<c>~/.pathhide/backups/</c>). Created
    /// lazily on the first run that actually writes an archive, so a launch with nothing to back up
    /// leaves the root untouched.</summary>
    public string BackupsDirectory => Path.Combine(HomeRoot, "backups");

    /// <summary>The backup change ledger, <c>backups/index.json</c> (see the data-backup conventions).</summary>
    public string BackupIndexFile => Path.Combine(BackupsDirectory, "index.json");
}

/// <summary>
/// UTC timestamp formatting for the backup feature, matching the fleet's timestamp conventions and the
/// <see cref="PathHide.Services.SessionLog"/> filename pattern. The archive stem is the
/// <c>yyyyMMdd-HHmmss-utc</c> file stamp; the index stores each file's last-write time as a whole-second
/// ISO-8601 UTC value (sub-second precision is deliberately dropped, since change is compared with a
/// two-second tolerance).
/// </summary>
public static class BackupTime
{
    private const string FileStampFormat = "yyyyMMdd-HHmmss";
    private const string IsoSecondsFormat = "yyyy-MM-ddTHH:mm:ss'Z'";

    private static readonly string[] AcceptedIsoFormats =
    {
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss.fffZ",
        "yyyy-MM-ddTHH:mm:sszzz",
        "yyyy-MM-ddTHH:mm:ss.fffzzz",
    };

    /// <summary>Filename-safe UTC stamp in the <c>yyyyMMdd-HHmmss-utc</c> convention (the archive stem).</summary>
    public static string FileStamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(FileStampFormat, CultureInfo.InvariantCulture) + "-utc";

    /// <summary>A whole-second UTC ISO-8601 stamp (<c>yyyy-MM-ddTHH:mm:ssZ</c>) for the index's stored
    /// modification time. Parses back through <see cref="TryParseIso"/>.</summary>
    public static string ToIsoSeconds(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(IsoSecondsFormat, CultureInfo.InvariantCulture);

    /// <summary>Attempts to parse a stored ISO-8601 timestamp leniently. Returns false instead of throwing
    /// so a hand-mangled index value is treated as a mismatch (recaptured) rather than failing the run.</summary>
    public static bool TryParseIso(string text, out DateTimeOffset value)
    {
        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

        return DateTimeOffset.TryParseExact(text, AcceptedIsoFormats, CultureInfo.InvariantCulture, styles, out value)
            || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out value);
    }
}
