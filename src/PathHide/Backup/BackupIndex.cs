using System.Collections.Generic;

namespace PathHide.Backup;

/// <summary>
/// The backup change ledger, serialized to <c>~/.pathhide/backups/index.json</c>. On disk it is a bare
/// JSON array of <see cref="BackupIndexEntry"/> (a <see cref="Storage.JsonStore{T}"/> over a
/// <see cref="List{T}"/> serializes the list itself, with no wrapper object) — one entry per captured
/// file state. It is at once the change ledger (deciding what a run must capture) and the table used to
/// locate a lost file later. See the data-backup conventions. <see cref="BackupIndex"/> is a static
/// helper namespace for that list; the list is the model.
/// </summary>
public static class BackupIndex
{
    /// <summary>An empty ledger — the normal state on a first run.</summary>
    public static List<BackupIndexEntry> Empty() => new();
}

/// <summary>
/// One captured file state. Fields are declared in the conventional order — the JSON serializer
/// preserves declaration order — so a record reads <c>{ archivedAt, archivePath, sizeBytes, lastWriteUtc }</c>.
/// There is no content hash: change is detected from size and modification time (see the data-backup
/// conventions).
/// </summary>
public sealed class BackupIndexEntry
{
    /// <summary>The capturing run's UTC file stamp (<c>yyyyMMdd-HHmmss-utc</c>). Also the stem of that run's
    /// archive, so the zip holding this entry is <c>backup-&lt;archivedAt&gt;.zip</c> — derived, never stored.</summary>
    public string ArchivedAt { get; set; } = string.Empty;

    /// <summary>The file's full entry path within the zip, e.g. <c>paths.json</c> or <c>config.json</c>.</summary>
    public string ArchivePath { get; set; } = string.Empty;

    /// <summary>The file's size in bytes at capture time.</summary>
    public long SizeBytes { get; set; }

    /// <summary>The file's last-write time in UTC, truncated to the whole second (<c>yyyy-MM-ddTHH:mm:ssZ</c>).</summary>
    public string LastWriteUtc { get; set; } = string.Empty;
}
