using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using PathHide.Backup;
using PathHide.Storage;
using PathHide.Tests.Storage;
using Xunit;

namespace PathHide.Tests.Backup;

/// <summary>
/// End-to-end backup runs over a throwaway <c>PATHHIDE_HOME</c>: a first run captures the durable files at
/// their mirror paths; an unchanged run writes nothing; an edit captures only what changed; a corrupt
/// index resets to a full backup; and a case-insensitive archive-path collision is skipped without failing
/// the run. Redirected through <c>PATHHIDE_HOME</c> — the one relocation seam — because the engine writes
/// the index through the real <see cref="JsonStore{T}"/>, which roots itself at <see cref="StorageRoot"/>.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class BackupEngineTests : IDisposable
{
    private static readonly DateTimeOffset Run1 = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Run2 = new(2026, 7, 1, 1, 0, 0, TimeSpan.Zero);

    private readonly string? _previousHome;
    private readonly string _home;
    private readonly BackupPaths _paths;

    public BackupEngineTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        _home = Path.Combine(Path.GetTempPath(), "pathhide-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _home);

        // The engine resolves its backups dir under the same root the index store uses.
        _paths = new BackupPaths(StorageRoot.Directory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_home, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private void WriteHomeFile(string name, string content) =>
        File.WriteAllText(Path.Combine(StorageRoot.Directory, name), content);

    private string[] ArchiveEntries(string archiveName)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(_paths.BackupsDirectory, archiveName));
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void First_Run_Captures_Config_And_Paths_At_Mirror_Paths()
    {
        WriteHomeFile("config.json", "{\"a\":1}");
        WriteHomeFile("paths.json", "[]");

        var report = new BackupEngine(_paths).Run(Run1);

        Assert.Null(report.Fatal);
        Assert.False(report.NothingChanged);
        Assert.Equal(2, report.FilesArchived);
        Assert.Equal("backup-20260701-000000-utc.zip", report.ArchiveFileName);

        var entries = ArchiveEntries("backup-20260701-000000-utc.zip");
        Assert.Contains("config.json", entries);
        Assert.Contains("paths.json", entries);

        Assert.True(File.Exists(_paths.BackupIndexFile));
        var indexJson = File.ReadAllText(_paths.BackupIndexFile);
        // The index is a JSON object with an `entries` array, camelCase keys, conventional field order.
        Assert.StartsWith("{", indexJson.TrimStart());
        Assert.Contains("\"entries\"", indexJson);
        Assert.Contains("\"archivedAt\": \"20260701-000000-utc\"", indexJson);
        Assert.Contains("\"archivePath\": \"config.json\"", indexJson);
        Assert.Contains("\"sizeBytes\"", indexJson);
        Assert.Contains("\"lastWriteUtc\"", indexJson);
    }

    [Fact]
    public void Feature_Owned_And_Excluded_Paths_Are_Not_Captured()
    {
        WriteHomeFile("config.json", "{\"a\":1}");
        WriteHomeFile("config.json.bak", "legacy sidecar");
        WriteHomeFile(".DS_Store", "junk");
        Directory.CreateDirectory(StorageRoot.LogsDirectory);
        File.WriteAllText(Path.Combine(StorageRoot.LogsDirectory, "session.log"), "log");

        var report = new BackupEngine(_paths).Run(Run1);

        Assert.Equal(1, report.FilesArchived);
        Assert.Equal(new[] { "config.json" }, ArchiveEntries("backup-20260701-000000-utc.zip"));
    }

    [Fact]
    public void Second_Run_With_No_Changes_Writes_Nothing()
    {
        WriteHomeFile("config.json", "{\"a\":1}");
        new BackupEngine(_paths).Run(Run1);

        var report = new BackupEngine(_paths).Run(Run2);

        Assert.True(report.NothingChanged);
        Assert.Null(report.ArchiveFileName);
        Assert.False(File.Exists(Path.Combine(_paths.BackupsDirectory, "backup-20260701-010000-utc.zip")));
    }

    [Fact]
    public void An_Edit_Captures_Only_The_Changed_File()
    {
        WriteHomeFile("config.json", "{\"a\":1}");
        WriteHomeFile("paths.json", "[]");
        new BackupEngine(_paths).Run(Run1);

        WriteHomeFile("config.json", "{\"a\":1,\"b\":2}"); // larger, so size differs regardless of mtime

        var report = new BackupEngine(_paths).Run(Run2);

        Assert.False(report.NothingChanged);
        Assert.Equal(1, report.FilesArchived);
        Assert.Equal(new[] { "config.json" }, ArchiveEntries("backup-20260701-010000-utc.zip"));
    }

    [Fact]
    public void A_Corrupt_Index_Is_Reset_And_Everything_Is_Recaptured()
    {
        WriteHomeFile("config.json", "{\"a\":1}");
        WriteHomeFile("paths.json", "[]");
        new BackupEngine(_paths).Run(Run1);

        File.WriteAllText(_paths.BackupIndexFile, "{ this is not valid json");

        var report = new BackupEngine(_paths).Run(Run2);

        Assert.True(report.IndexWasReset);
        Assert.Equal(2, report.FilesArchived);
    }

    [Fact]
    public void A_CaseInsensitive_Collision_Is_Skipped_And_The_Run_Continues()
    {
        // Two files whose names fold to one archive path can only coexist on a case-sensitive filesystem.
        if (!FilesystemIsCaseSensitive())
        {
            return;
        }

        WriteHomeFile("config.json", "{\"a\":1}");
        File.WriteAllText(Path.Combine(StorageRoot.Directory, "Config.json"), "{\"b\":2}");

        var report = new BackupEngine(_paths).Run(Run1);

        Assert.False(report.NothingChanged);
        Assert.Equal(1, report.FilesArchived);
        Assert.Contains(report.Skips, s => s.Reason.Contains("collision", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BackupsDirectory_Is_Owner_Only_On_Posix()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        WriteHomeFile("config.json", "{\"a\":1}");
        new BackupEngine(_paths).Run(Run1);

        var mode = File.GetUnixFileMode(_paths.BackupsDirectory);
        var groupOrOther =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

        Assert.Equal((UnixFileMode)0, mode & groupOrOther);
    }

    private bool FilesystemIsCaseSensitive()
    {
        var probe = Path.Combine(StorageRoot.Directory, "CaseProbe.tmp");
        try
        {
            File.WriteAllText(probe, "x");
            var sensitive = !File.Exists(Path.Combine(StorageRoot.Directory, "caseprobe.tmp"));
            File.Delete(probe);
            return sensitive;
        }
        catch
        {
            return false;
        }
    }
}
