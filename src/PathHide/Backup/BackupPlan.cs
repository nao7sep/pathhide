using System;
using System.Collections.Generic;

namespace PathHide.Backup;

/// <summary>
/// The pure change decision: given the current candidates and the existing index, returns the ones a run
/// must capture. A candidate is captured when its <c>(size, mtime)</c> differs from the latest recorded
/// state for its archive path — where two modification times within <see cref="MtimeMatchToleranceSeconds"/>
/// count as equal. No content hashing (see the data-backup conventions); every real edit moves the mtime,
/// and the tolerance absorbs FAT/exFAT's two-second granularity on USB drives.
/// </summary>
public static class BackupPlan
{
    /// <summary>
    /// The modification-time equality window, in seconds. Two seconds absorbs FAT/exFAT's 2-second mtime
    /// granularity; it costs nothing in missed edits because the recorded time is from a prior backup
    /// run, which any real edit moves well beyond two seconds past.
    /// </summary>
    public const int MtimeMatchToleranceSeconds = 2;

    /// <summary>Returns the candidates whose size or modification time differs from the latest index entry
    /// for their archive path (a candidate with no prior entry is always new).</summary>
    public static IReadOnlyList<BackupCandidate> SelectChanged(
        IReadOnlyList<BackupCandidate> candidates,
        IReadOnlyList<BackupIndexEntry> index)
    {
        var latest = LatestByPath(index);
        var changed = new List<BackupCandidate>();
        foreach (var candidate in candidates)
        {
            if (IsChanged(candidate, latest))
            {
                changed.Add(candidate);
            }
        }

        return changed;
    }

    private static bool IsChanged(BackupCandidate candidate, IReadOnlyDictionary<string, BackupIndexEntry> latest)
    {
        if (!latest.TryGetValue(candidate.ArchivePath, out var entry))
        {
            return true;
        }

        if (entry.SizeBytes != candidate.SizeBytes)
        {
            return true;
        }

        // A stored timestamp that cannot be parsed (a hand-mangled index) is treated as a mismatch, so the
        // file is recaptured rather than silently trusted.
        if (!BackupTime.TryParseIso(entry.LastWriteUtc, out var recorded))
        {
            return true;
        }

        var delta = Math.Abs((candidate.LastWriteUtc - recorded).TotalSeconds);
        return delta > MtimeMatchToleranceSeconds;
    }

    /// <summary>The latest entry per archive path. <c>archivedAt</c> is a <c>yyyyMMdd-HHmmss-utc</c> stamp,
    /// so ordinal string comparison is chronological.</summary>
    private static Dictionary<string, BackupIndexEntry> LatestByPath(IReadOnlyList<BackupIndexEntry> index)
    {
        var latest = new Dictionary<string, BackupIndexEntry>(StringComparer.Ordinal);
        foreach (var entry in index)
        {
            if (!latest.TryGetValue(entry.ArchivePath, out var current) ||
                string.CompareOrdinal(entry.ArchivedAt, current.ArchivedAt) >= 0)
            {
                latest[entry.ArchivePath] = entry;
            }
        }

        return latest;
    }
}
