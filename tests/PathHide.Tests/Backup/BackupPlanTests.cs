using System;
using System.Collections.Generic;
using System.Linq;
using PathHide.Backup;
using Xunit;

namespace PathHide.Tests.Backup;

/// <summary>
/// The pure change decision: a candidate is captured when it is new, when its size differs, or when its
/// mtime differs by more than the two-second tolerance. No I/O.
/// </summary>
public sealed class BackupPlanTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 7, 1, 2, 22, 20, TimeSpan.Zero);

    private static BackupIndexEntry Entry(string path, long size, DateTimeOffset mtime) => new()
    {
        ArchivedAt = "20260701-000000-utc",
        ArchivePath = path,
        SizeBytes = size,
        LastWriteUtc = BackupTime.ToIsoSeconds(mtime),
    };

    private static BackupCandidate Candidate(string path, long size, DateTimeOffset mtime) =>
        new("/src/" + path, path, size, mtime);

    [Fact]
    public void NoPriorEntry_IsNew()
    {
        var changed = BackupPlan.SelectChanged(
            new[] { Candidate("config.json", 10, Baseline) },
            new List<BackupIndexEntry>());

        Assert.Single(changed);
    }

    [Fact]
    public void SameSizeAndMtime_IsUnchanged()
    {
        var changed = BackupPlan.SelectChanged(
            new[] { Candidate("config.json", 10, Baseline) },
            new[] { Entry("config.json", 10, Baseline) });

        Assert.Empty(changed);
    }

    [Fact]
    public void DifferentSize_IsChanged()
    {
        var changed = BackupPlan.SelectChanged(
            new[] { Candidate("config.json", 11, Baseline) },
            new[] { Entry("config.json", 10, Baseline) });

        Assert.Single(changed);
    }

    [Fact]
    public void MtimeWithinTolerance_IsUnchanged()
    {
        var changed = BackupPlan.SelectChanged(
            new[] { Candidate("config.json", 10, Baseline.AddSeconds(2)) },
            new[] { Entry("config.json", 10, Baseline) });

        Assert.Empty(changed);
    }

    [Fact]
    public void MtimeBeyondTolerance_IsChanged()
    {
        var changed = BackupPlan.SelectChanged(
            new[] { Candidate("config.json", 10, Baseline.AddSeconds(3)) },
            new[] { Entry("config.json", 10, Baseline) });

        Assert.Single(changed);
    }

    [Fact]
    public void UnparseableStoredMtime_IsTreatedAsChanged()
    {
        var entry = Entry("config.json", 10, Baseline);
        entry.LastWriteUtc = "garbage";

        var changed = BackupPlan.SelectChanged(
            new[] { Candidate("config.json", 10, Baseline) },
            new[] { entry });

        Assert.Single(changed);
    }

    [Fact]
    public void LatestEntryPerPath_Wins()
    {
        // A later archivedAt records the current state; an older, differing entry must not force a recapture.
        var older = Entry("config.json", 10, Baseline);
        older.ArchivedAt = "20260701-000000-utc";
        var newer = Entry("config.json", 20, Baseline);
        newer.ArchivedAt = "20260701-010000-utc";

        var changed = BackupPlan.SelectChanged(
            new[] { Candidate("config.json", 20, Baseline) },
            new[] { older, newer });

        Assert.Empty(changed);
    }
}
