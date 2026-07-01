using System;
using System.Collections.Generic;
using System.IO;

namespace PathHide.Backup;

/// <summary>
/// Discovers what to back up by walking the app's home root under <c>~/.pathhide/</c>. The backup is
/// HOME-ROOT-ONLY — PathHide keeps all its durable data (<c>config.json</c>, <c>paths.json</c>) inside the
/// root and tracks no external managed roots — so this is a single recursive walk minus the exclusions.
/// It produces the stat'd candidates for <see cref="BackupPlan"/> and records a <see cref="BackupSkip"/>
/// for any directory it cannot enumerate, any file it cannot stat, and any case-insensitive archive-path
/// collision. All I/O here is metadata only — directory walks and <see cref="FileInfo"/>; file contents
/// are read later, when a changed file is archived.
/// </summary>
public sealed class BackupRootCollector
{
    private readonly BackupPaths _paths;

    public BackupRootCollector(BackupPaths paths) => _paths = paths;

    public CollectedRoots Collect()
    {
        var candidates = new List<BackupCandidate>();
        var skips = new List<BackupSkip>();

        // Two candidates whose archive paths fold to the same value on a case-insensitive comparison would
        // collide as one zip entry; keep the first seen and record a skip for the rest.
        var seenArchivePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(_paths.HomeRoot))
        {
            WalkHome(_paths.HomeRoot, _paths.HomeRoot, candidates, skips, seenArchivePaths);
        }

        return new CollectedRoots(candidates, skips);
    }

    /// <summary>Walks <c>~/.pathhide/</c>, pruning the excluded <c>logs/</c> and <c>backups/</c> subtrees
    /// rather than walking and discarding them (backups/ can grow large).</summary>
    private void WalkHome(
        string root,
        string directory,
        List<BackupCandidate> candidates,
        List<BackupSkip> skips,
        Dictionary<string, string> seenArchivePaths)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex)
        {
            skips.Add(new BackupSkip(directory, "could not enumerate: " + ex.Message));
            return;
        }

        foreach (var entry in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch (Exception ex)
            {
                skips.Add(new BackupSkip(entry, "could not stat: " + ex.Message));
                continue;
            }

            // Never follow a symlink/junction — silently skip it (it is not the app's own data, and
            // following it risks a walk loop or an escape outside the root). Only real directories and
            // regular files are considered (data-backup conventions' traversal rules).
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var relative = BackupArchivePaths.Normalize(Path.GetRelativePath(root, entry));
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                // Prune an excluded subtree (logs/, backups/) instead of descending into it.
                if (!HomeRootExclusions.IsExcluded(relative + "/"))
                {
                    WalkHome(root, entry, candidates, skips, seenArchivePaths);
                }
            }
            else if (!HomeRootExclusions.IsExcluded(relative))
            {
                var archivePath = BackupArchivePaths.ForHomeFile(relative);
                if (seenArchivePaths.TryGetValue(archivePath, out var kept))
                {
                    skips.Add(new BackupSkip(
                        entry,
                        "case-insensitive archive-path collision with '" + kept + "'; keeping the first"));
                    continue;
                }

                if (TryStat(entry, archivePath, candidates, skips))
                {
                    seenArchivePaths[archivePath] = entry;
                }
            }
        }
    }

    private static bool TryStat(
        string sourcePath, string archivePath, List<BackupCandidate> candidates, List<BackupSkip> skips)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            candidates.Add(new BackupCandidate(
                sourcePath, archivePath, info.Length, ToWholeSecondUtc(info.LastWriteTimeUtc)));
            return true;
        }
        catch (Exception ex)
        {
            skips.Add(new BackupSkip(sourcePath, "could not stat: " + ex.Message));
            return false;
        }
    }

    private static DateTimeOffset ToWholeSecondUtc(DateTime lastWriteUtc)
    {
        var utc = DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc);
        return new DateTimeOffset(utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond)), TimeSpan.Zero);
    }
}

/// <summary>The candidates and skips a collection pass produced.</summary>
public sealed record CollectedRoots(
    IReadOnlyList<BackupCandidate> Candidates,
    IReadOnlyList<BackupSkip> Skips);
