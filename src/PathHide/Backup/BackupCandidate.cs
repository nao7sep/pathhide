using System;

namespace PathHide.Backup;

/// <summary>
/// One file the selection has decided to consider for backup, already stat'd. <see cref="SourcePath"/>
/// is the absolute path on disk (read only when the file is actually archived); <see cref="ArchivePath"/>
/// is its mirror-layout entry path within the zip; <see cref="SizeBytes"/> and <see cref="LastWriteUtc"/>
/// are the change signal, the latter truncated to the whole second.
/// </summary>
public sealed record BackupCandidate(
    string SourcePath,
    string ArchivePath,
    long SizeBytes,
    DateTimeOffset LastWriteUtc);
