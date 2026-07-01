using System;
using System.Collections.Generic;

namespace PathHide.Backup;

/// <summary>
/// The outcome of one backup run, returned by <see cref="BackupEngine"/> so the caller — the only place
/// that logs — can record it. The engine never throws for an expected problem: a fatal error is captured
/// in <see cref="Fatal"/>, a single unreadable file is a <see cref="BackupSkip"/>, and a case-insensitive
/// entry collision is a <see cref="BackupSkip"/> too.
/// </summary>
public sealed class BackupReport
{
    /// <summary>Nothing changed since the last run, so no archive and no index write happened.</summary>
    public bool NothingChanged { get; init; }

    /// <summary>The archive written this run (<c>backup-&lt;archivedAt&gt;.zip</c>), or null when nothing was written.</summary>
    public string? ArchiveFileName { get; init; }

    /// <summary>How many files the archive contains.</summary>
    public int FilesArchived { get; init; }

    /// <summary>Files skipped (unreadable, or a case-insensitive archive-path collision), each with a reason.</summary>
    public IReadOnlyList<BackupSkip> Skips { get; init; } = Array.Empty<BackupSkip>();

    /// <summary>A corrupt index was found, reset, and treated as empty — this run is a full backup.</summary>
    public bool IndexWasReset { get; init; }

    /// <summary>An unexpected failure the engine caught rather than propagating; null on success.</summary>
    public Exception? Fatal { get; init; }
}

/// <summary>A file the run could not capture, with the reason it was passed over.</summary>
public sealed record BackupSkip(string Path, string Reason);
