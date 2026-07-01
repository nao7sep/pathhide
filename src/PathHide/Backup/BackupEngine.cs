using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using PathHide.Storage;

namespace PathHide.Backup;

/// <summary>
/// Runs one backup pass and returns a <see cref="BackupReport"/>. It never throws for an expected problem
/// (a fatal error is captured in the report) and never logs — the caller logs the report. See the
/// data-backup conventions: change is size + mtime, the archive mirrors <c>~/.pathhide/</c>, and the
/// archive is written and renamed into place <em>before</em> the index so a crash never records a phantom
/// backup. Kept free of Avalonia/UI dependencies so it (and the pure classes it drives) unit-tests.
/// </summary>
public sealed class BackupEngine
{
    private readonly BackupPaths _paths;

    public BackupEngine(BackupPaths paths) => _paths = paths;

    /// <summary>Captures everything changed since the last run. <paramref name="now"/> is injected so the
    /// archive stamp is deterministic under test.</summary>
    public BackupReport Run(DateTimeOffset now)
    {
        try
        {
            return RunCore(now);
        }
        catch (Exception ex)
        {
            return new BackupReport { Fatal = ex };
        }
    }

    private BackupReport RunCore(DateTimeOffset now)
    {
        var (index, indexReset) = LoadIndex();

        var collected = new BackupRootCollector(_paths).Collect();
        var skips = new List<BackupSkip>(collected.Skips);

        var changed = BackupPlan.SelectChanged(collected.Candidates, index);
        if (changed.Count == 0)
        {
            return new BackupReport { NothingChanged = true, Skips = skips, IndexWasReset = indexReset };
        }

        var archivedAt = BackupTime.FileStamp(now);
        var archived = WriteArchive(archivedAt, changed, skips);
        if (archived.Count == 0)
        {
            // Every changed file failed to read at archive time; nothing was written, so nothing is recorded.
            return new BackupReport { NothingChanged = true, Skips = skips, IndexWasReset = indexReset };
        }

        foreach (var item in archived)
        {
            index.Add(new BackupIndexEntry
            {
                ArchivedAt = archivedAt,
                ArchivePath = item.ArchivePath,
                SizeBytes = item.SizeBytes,
                LastWriteUtc = BackupTime.ToIsoSeconds(item.LastWriteUtc),
            });
        }

        // Index second: the archive is already safely in place, so a crash here just re-captures next run.
        IndexStore().Save(new BackupIndex { Entries = index });

        return new BackupReport
        {
            ArchiveFileName = ArchiveFileName(archivedAt),
            FilesArchived = archived.Count,
            Skips = skips,
            IndexWasReset = indexReset,
        };
    }

    // The index is a JSON object wrapping an `entries` array (see the data-backup conventions), stored via
    // a JsonStore over BackupIndex. "backups/index.json" is relative to the backups dir, which lives under
    // the storage root, so the store's file-name-under-root convention resolves it.
    private static JsonStore<BackupIndex> IndexStore() =>
        new(Path.Combine("backups", "index.json"), "backup index");

    private (List<BackupIndexEntry> Index, bool Reset) LoadIndex()
    {
        if (!File.Exists(_paths.BackupIndexFile))
        {
            // Missing index is the normal first run: an empty ledger, everything is new. Not a reset.
            return (new List<BackupIndexEntry>(), false);
        }

        try
        {
            var json = File.ReadAllText(_paths.BackupIndexFile);
            var index = JsonSerializer.Deserialize<BackupIndex>(json, JsonOptions.Default);
            return (index?.Entries ?? new List<BackupIndexEntry>(), false);
        }
        catch
        {
            // A corrupt index is reset to empty: the run becomes a full backup, which costs one redundant
            // archive, never data. The stale file is left to be atomically overwritten by this run's save.
            return (new List<BackupIndexEntry>(), true);
        }
    }

    /// <summary>Writes the changed files to a temp zip and moves it into place, returning the files that
    /// were actually archived (a file unreadable at archive time is skipped, not recorded).</summary>
    private List<BackupCandidate> WriteArchive(
        string archivedAt, IReadOnlyList<BackupCandidate> changed, List<BackupSkip> skips)
    {
        EnsureBackupsDirectory();
        var finalPath = Path.Combine(_paths.BackupsDirectory, ArchiveFileName(archivedAt));
        var tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        var archived = new List<BackupCandidate>();
        try
        {
            using (var zip = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                foreach (var item in changed)
                {
                    try
                    {
                        zip.CreateEntryFromFile(item.SourcePath, item.ArchivePath);
                        archived.Add(item);
                    }
                    catch (Exception ex)
                    {
                        skips.Add(new BackupSkip(item.ArchivePath, "unreadable at archive time: " + ex.Message));
                    }
                }
            }

            if (archived.Count == 0)
            {
                TryDelete(tempPath);
                return archived;
            }

            // Atomic publish: move the fully written temp zip over the final name (replacing a same-second
            // rerun's file if one exists) so a reader never sees a half-written archive.
            File.Move(tempPath, finalPath, overwrite: true);
            return archived;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Creates <c>backups/</c> lazily with the platform's default mode. Secrets are excluded from
    /// backups fleet-wide, so no archive can contain a secret and the directory needs no permission
    /// hardening (see the data-backup conventions).</summary>
    private void EnsureBackupsDirectory()
    {
        Directory.CreateDirectory(_paths.BackupsDirectory);
    }

    private static string ArchiveFileName(string archivedAt) => "backup-" + archivedAt + ".zip";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort: a leftover temp is harmless and under backups/, which the walk excludes.
        }
    }
}
