using System.Collections.Generic;

namespace PathHide.Backup;

/// <summary>
/// The backup change ledger, serialized to <c>~/.pathhide/backups/index.json</c> as a JSON <b>object</b>
/// with an <see cref="Entries"/> array — one entry per captured file state. The object wrapper (rather
/// than a bare array) leaves room for future top-level metadata, e.g. a schema <c>version</c>, without
/// disturbing the records (the fleet-wide shape, per the data-backup conventions). It is at once the
/// change ledger (deciding what a run must capture) and the table used to locate a lost file later.
/// </summary>
public sealed class BackupIndex
{
    /// <summary>The captured file states; empty on a first run.</summary>
    public List<BackupIndexEntry> Entries { get; set; } = new();
}

/// <summary>
/// One captured file state. Fields are declared in the conventional order — the JSON serializer
/// preserves declaration order — so a record reads <c>{ archivedAt, archivePath, sizeBytes, lastWriteUtc }</c>.
/// There is no content hash: change is detected from size and modification time (see the data-backup
/// conventions).
/// </summary>
public sealed class BackupIndexEntry
{
    /// <summary>The capturing run's UTC file stamp (<c>yyyyMMdd-HHmmss-fff-utc</c>; a pre-rollout entry may
    /// carry the older whole-second <c>yyyyMMdd-HHmmss-utc</c> form, left as-is). Also the stem of that
    /// run's archive, so the zip holding this entry is <c>backup-&lt;archivedAt&gt;.zip</c> — derived, never
    /// stored.</summary>
    public string ArchivedAt { get; set; } = string.Empty;

    /// <summary>The file's full entry path within the zip, e.g. <c>paths.json</c> or <c>config.json</c>.</summary>
    public string ArchivePath { get; set; } = string.Empty;

    /// <summary>The file's size in bytes at capture time.</summary>
    public long SizeBytes { get; set; }

    /// <summary>The file's last-write time in UTC, truncated to the whole second (<c>yyyy-MM-ddTHH:mm:ssZ</c>).</summary>
    public string LastWriteUtc { get; set; } = string.Empty;
}
