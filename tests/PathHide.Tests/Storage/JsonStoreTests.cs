using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PathHide.Backup;
using PathHide.Models;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>
/// Exercises the real file I/O of <see cref="JsonStore{T}"/> against a temp
/// directory redirected via the <c>PATHHIDE_HOME</c> environment variable — the one
/// relocation seam, used the same way in tests and production. These touch the
/// disk on purpose: the atomic write is the behaviour that protects the user's
/// saved data, and a fake filesystem would not exercise it. There is no longer a
/// <c>.bak</c> sidecar (retired per the data-backup conventions); several tests
/// assert that no such file is ever created.
/// </summary>
/// <remarks>
/// Every real <see cref="JsonStore{T}.Save"/> here also drives the write-through
/// <see cref="BackupStore"/> (the record fires after the atomic rename). The
/// store is a process-wide singleton, so <see cref="Dispose"/> closes it before
/// deleting the throwaway root — that releases its <c>backups.sqlite3</c> handle
/// (so the delete succeeds) and forces the next test to re-open against its own
/// fresh <c>PATHHIDE_HOME</c>, rather than keep writing into this test's root.
/// </remarks>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class JsonStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;

    public JsonStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pathhide-tests", NanoId.New());
        Directory.CreateDirectory(_root);

        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
        // Close any store left open by a prior test so this test's first Save opens the store fresh
        // against this test's root, not a stale handle to an already-deleted directory.
        BackupStore.Close();
    }

    public void Dispose()
    {
        // Release the backups.sqlite3 handle before deleting the root, and reset the singleton so the next
        // throwaway root re-opens its own store.
        BackupStore.Close();
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string PathOf(string fileName) => Path.Combine(_root, fileName);

    [Fact]
    public void SaveThenLoad_RoundTripsValue()
    {
        var store = new JsonStore<AppSettings>("config.json", "settings");
        store.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenAndSystem });

        var loaded = store.Load();

        Assert.Equal(WindowsHideMode.HiddenAndSystem, loaded.WindowsHideMode);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var store = new JsonStore<AppSettings>("config.json", "settings");

        var loaded = store.Load();

        // Default-constructed AppSettings.
        Assert.Equal(WindowsHideMode.HiddenOnly, loaded.WindowsHideMode);
        Assert.False(File.Exists(PathOf("config.json")));
    }

    [Fact]
    public void Load_CorruptPrimary_QuarantinesFileAndReturnsDefault_WithNoBakFallback()
    {
        // The .bak last-good sidecar is retired: an unreadable live file is quarantined (moved aside,
        // bytes preserved) rather than reset over — the storage-path conventions' quarantine-then-reset
        // path — and the caller falls back to defaults. Never a .bak.
        var store = new JsonStore<List<PathEntry>>("paths.json", "paths");
        store.Save([new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }]);
        store.Save([
            new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden },
            new PathEntry { Path = "/b", DesiredVisibility = DesiredVisibility.Shown },
        ]);

        const string corrupt = "{ not valid json";
        File.WriteAllText(PathOf("paths.json"), corrupt);

        var loaded = store.Load();

        Assert.Empty(loaded);
        Assert.False(File.Exists(PathOf("paths.json")));
        Assert.False(File.Exists(PathOf("paths.json.bak")));

        // Quarantined under the grammar-shaped name <stem>-<millisecond-utc-stamp>.invalid, in the
        // same directory, with the original (corrupt) bytes intact.
        var quarantined = Directory.EnumerateFiles(_root, "paths-*.invalid").ToList();
        Assert.Single(quarantined);
        Assert.Matches(@"^paths-\d{8}-\d{6}-\d{3}-utc\.invalid$", Path.GetFileName(quarantined[0]));
        Assert.Equal(corrupt, File.ReadAllText(quarantined[0]));
    }

    [Fact]
    public void Save_AfterQuarantine_RecreatesLiveFileAndNeverTouchesTheQuarantinedFile()
    {
        var store = new JsonStore<List<PathEntry>>("paths.json", "paths");
        const string corrupt = "{ not valid json";
        File.WriteAllText(PathOf("paths.json"), corrupt);

        store.Load();
        var quarantinedPath = Directory.EnumerateFiles(_root, "paths-*.invalid").Single();

        store.Save([new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }]);

        // The next save recreates the live file (the first-run materialization path's counterpart for an
        // in-flight store) without ever touching the quarantined file sitting beside it.
        Assert.True(File.Exists(PathOf("paths.json")));
        Assert.Single(store.Load());
        Assert.Equal(corrupt, File.ReadAllText(quarantinedPath));
    }

    [Fact]
    public void QuarantinePath_IsStemHyphenMillisecondUtcStampDotInvalid_InTheSameDirectory()
    {
        // Derived-filename grammar: <stem>-<discriminator>.invalid, one role extension, the same
        // millisecond UTC stamp form the data-backup engine's archive names use.
        var targetPath = PathOf("config.json");
        var timestamp = new DateTimeOffset(2026, 7, 1, 2, 22, 20, 7, TimeSpan.Zero);

        var quarantinePath = JsonStore<AppSettings>.QuarantinePath(targetPath, timestamp);

        Assert.Equal(PathOf("config-20260701-022220-007-utc.invalid"), quarantinePath);
    }

    [Fact]
    public void Load_LiteralNullDocument_ReturnsDefault()
    {
        File.WriteAllText(PathOf("config.json"), "null");
        var store = new JsonStore<AppSettings>("config.json", "settings");

        var loaded = store.Load();

        Assert.Equal(WindowsHideMode.HiddenOnly, loaded.WindowsHideMode);
    }

    [Fact]
    public void Save_FirstTime_CreatesLiveFileAndNoBak()
    {
        var store = new JsonStore<AppSettings>("config.json", "settings");

        store.Save(new AppSettings());

        Assert.True(File.Exists(PathOf("config.json")));
        // The .bak sidecar is retired: a first save writes exactly the live file.
        Assert.False(File.Exists(PathOf("config.json.bak")));
    }

    [Fact]
    public void Save_SecondTime_ReplacesLiveFileAndWritesNoBak()
    {
        var store = new JsonStore<AppSettings>("config.json", "settings");
        store.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenOnly });
        store.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenAndSystem });

        var liveJson = File.ReadAllText(PathOf("config.json"));

        // The second save atomically replaces the live file with no last-good copy left beside it.
        Assert.Contains("hidden_and_system", liveJson);
        Assert.False(File.Exists(PathOf("config.json.bak")));
    }

    [Fact]
    public void Save_LeavesNoTempFiles()
    {
        var store = new JsonStore<AppSettings>("config.json", "settings");
        store.Save(new AppSettings());
        store.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenAndSystem });

        var temps = Directory.EnumerateFiles(_root, "*.tmp").ToList();

        Assert.Empty(temps);
    }

    [Fact]
    public void TempPath_IsStemHyphenDiscriminatorDotTmp_InTheSameDirectory()
    {
        // Derived-filename grammar: <stem>-<discriminator>.tmp, one role extension, never a
        // dot-appended suffix like "config.json.<x>.tmp".
        var targetPath = PathOf("config.json");

        var tempPath = JsonStore<AppSettings>.TempPath(targetPath, "abc123");

        Assert.Equal(PathOf("config-abc123.tmp"), tempPath);
    }

    [Fact]
    public void Save_WritesCamelCasePropertiesAndSnakeCaseEnums()
    {
        var store = new JsonStore<AppSettings>("config.json", "settings");
        store.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenAndSystem });

        var json = File.ReadAllText(PathOf("config.json"));

        // Locks the on-disk shape so a serializer-option change can't silently
        // orphan existing user files.
        Assert.Contains("\"windowsHideMode\"", json);
        Assert.Contains("\"hidden_and_system\"", json);
    }

    [Fact]
    public void CreateIfMissing_WritesDefaultsOnFirstRun()
    {
        var store = new JsonStore<AppSettings>("config.json", "settings");

        var created = store.CreateIfMissing(new AppSettings());

        Assert.True(created);
        Assert.True(File.Exists(PathOf("config.json")));
        // Produced through Save (the real serializer), so it round-trips and carries the on-disk shape.
        Assert.Contains("\"windowsHideMode\"", File.ReadAllText(PathOf("config.json")));
        Assert.Equal(WindowsHideMode.HiddenOnly, store.Load().WindowsHideMode);
    }

    [Fact]
    public void CreateIfMissing_NeverTouchesAnExistingFile()
    {
        var store = new JsonStore<AppSettings>("config.json", "settings");
        store.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenAndSystem });
        var before = File.ReadAllText(PathOf("config.json"));

        // A different value must not overwrite: absence is the single trigger, so a good (possibly
        // hand-edited) file is left byte-for-byte as it was.
        var created = store.CreateIfMissing(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenOnly });

        Assert.False(created);
        Assert.Equal(before, File.ReadAllText(PathOf("config.json")));
        Assert.Equal(WindowsHideMode.HiddenAndSystem, store.Load().WindowsHideMode);
    }

    [Fact]
    public void SettingsAndPaths_ResolveToDistinctFiles()
    {
        // The durable settings live in config.json; the user's path list lives in
        // paths.json. They are separate roles and must never collapse onto one file.
        // This guards the settings-file rename.
        var settingsStore = new JsonStore<AppSettings>("config.json", "settings");
        var pathListStore = new JsonStore<List<PathEntry>>("paths.json", "paths");

        settingsStore.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenAndSystem });
        pathListStore.Save([new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }]);

        Assert.True(File.Exists(PathOf("config.json")));
        Assert.True(File.Exists(PathOf("paths.json")));

        // The .bak sidecar is retired: neither store leaves one behind.
        Assert.False(File.Exists(PathOf("config.json.bak")));
        Assert.False(File.Exists(PathOf("paths.json.bak")));

        // No stale settings.json is produced by the settings store any longer.
        Assert.False(File.Exists(PathOf("settings.json")));
        Assert.False(File.Exists(PathOf("settings.json.bak")));

        // Each store round-trips only its own document; the roles do not bleed together.
        Assert.Equal(WindowsHideMode.HiddenAndSystem, settingsStore.Load().WindowsHideMode);
        Assert.Single(pathListStore.Load());
    }

    // --- Write-through data-backup hook (STEP 3): the record fires from this one choke point, after the
    // rename, for both managed files, byte-identically to what landed on disk, keyed by the FINAL path. ---

    /// <summary>Reads the recorded content blob(s) for a path from the throwaway root's backups.sqlite3,
    /// opening a private read-only connection.</summary>
    private List<byte[]> RecordedContentsFor(string absolutePath)
    {
        var contents = new List<byte[]>();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = PathOf("backups.sqlite3"),
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM backups WHERE path = $path ORDER BY id ASC";
        command.Parameters.AddWithValue("$path", absolutePath);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var length = reader.GetBytes(0, 0, null, 0, 0);
            var buffer = new byte[length];
            reader.GetBytes(0, 0, buffer, 0, buffer.Length);
            contents.Add(buffer);
        }
        return contents;
    }

    /// <summary>Every distinct <c>path</c> value recorded in the throwaway root's store.</summary>
    private List<string> RecordedPaths()
    {
        var paths = new List<string>();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = PathOf("backups.sqlite3"),
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT path FROM backups";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            paths.Add(reader.GetString(0));
        return paths;
    }

    [Fact]
    public void Save_RecordsTheExactBytesOnDisk_KeyedByTheFinalPath_NeverTheTemp()
    {
        var store = new JsonStore<AppSettings>("config.json", "settings");
        store.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenAndSystem });

        var onDisk = File.ReadAllBytes(PathOf("config.json"));
        var recorded = RecordedContentsFor(PathOf("config.json"));

        // One row, byte-identical to the file the atomic rename left in place — keyed by the final path.
        Assert.Equal(onDisk, Assert.Single(recorded));

        // The only recorded key is the final file; the atomic-write temp is never a recorded path (the
        // record fires strictly after the rename, on the final path, reusing the in-hand bytes).
        Assert.Equal(new[] { PathOf("config.json") }, RecordedPaths());
    }

    [Fact]
    public void Save_RecordsBothManagedFiles_ConfigAndPaths()
    {
        // Both managed text files are recorded on save: config.json (durable settings) and paths.json (the
        // user's tracked path list). Neither is excluded; the store's default is to capture managed text.
        new JsonStore<AppSettings>("config.json", "settings").Save(new AppSettings());
        new JsonStore<List<PathEntry>>("paths.json", "paths")
            .Save([new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }]);

        Assert.Single(RecordedContentsFor(PathOf("config.json")));
        Assert.Single(RecordedContentsFor(PathOf("paths.json")));
    }

    [Fact]
    public void Save_UnchangedResave_RecordsNoSecondVersion_ButAChangedSaveDoes()
    {
        var store = new JsonStore<AppSettings>("config.json", "settings");
        store.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenOnly });
        store.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenOnly });  // identical -> deduped
        Assert.Single(RecordedContentsFor(PathOf("config.json")));

        store.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenAndSystem }); // changed -> row
        Assert.Equal(2, RecordedContentsFor(PathOf("config.json")).Count);
    }

    [Fact]
    public void Save_FailedRecord_NeverBreaksTheSave()
    {
        // Close the store, then stand a *file* where its backups.sqlite3 belongs so the next open fails.
        // The save must still fully succeed (file on disk, round-trips) — the backup is best-effort and can
        // never break a save that already landed.
        BackupStore.Close();
        File.WriteAllText(PathOf("backups.sqlite3"), "not a database");

        var store = new JsonStore<AppSettings>("config.json", "settings");
        var exception = Xunit.Record.Exception(() =>
            store.Save(new AppSettings { WindowsHideMode = WindowsHideMode.HiddenAndSystem }));

        Assert.Null(exception);
        Assert.True(File.Exists(PathOf("config.json")));
        Assert.Equal(WindowsHideMode.HiddenAndSystem, store.Load().WindowsHideMode);
    }
}
