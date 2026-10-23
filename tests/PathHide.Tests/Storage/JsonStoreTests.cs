using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using PathHide.Backup;
using PathHide.Models;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>
/// Exercises the real file I/O of <see cref="JsonStore{T}"/> against a temp
/// directory redirected via the <c>PATHHIDE_DATA_DIR</c> environment variable — the one
/// relocation seam, used the same way in tests and production. These touch the
/// disk on purpose: the atomic write is the behaviour that protects the user's
/// saved data, and a fake filesystem would not exercise it. History lives in the backup
/// database, so a save leaves nothing beside the live document.
/// </summary>
/// <remarks>
/// Every real <see cref="JsonStore{T}.Save"/> here also queues a write for the
/// <see cref="BackupStore"/> (handed over after the atomic rename). The store is a
/// process-wide singleton, so <see cref="Dispose"/> closes it — waiting for pending
/// writes — before deleting the throwaway root. That releases its <c>backups.sqlite3</c>
/// handle (so the delete succeeds) and forces the next test to re-open against its own
/// fresh <c>PATHHIDE_DATA_DIR</c>, rather than keep writing into this test's root.
/// </remarks>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class JsonStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;

    public JsonStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pathhide-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _root);
        // Close any store left open by a prior test so this test's first Save opens the store fresh
        // against this test's root, not a stale handle to an already-deleted directory.
        BackupStore.Close();
    }

    public void Dispose()
    {
        // Release the backups.sqlite3 handle before deleting the root, and reset the singleton so the next
        // throwaway root re-opens its own store.
        BackupStore.Close();
        // Closing returns each connection to Microsoft.Data.Sqlite's pool, which keeps its file open;
        // Windows cannot delete an open database file.
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _previousHome);
        Directory.Delete(_root, recursive: true);
    }

    private string PathOf(string fileName) => Path.Combine(_root, fileName);

    /// <summary>A store that falls back to defaults for what it cannot use, as config.json's does.</summary>
    private static JsonStore<TestDocument> SettingsDocumentStore() =>
        new("config.json", "settings", FormatVersions.Settings);

    public sealed class TestDocument
    {
        public WindowsHideMode WindowsHideMode { get; set; }
        public ThemePreference Theme { get; set; }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValue()
    {
        var store = new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings);
        store.Save(new TestDocument
        {
            WindowsHideMode = WindowsHideMode.HiddenAndSystem,
            Theme = ThemePreference.Dark,
        });

        var loaded = store.Load().Value;

        Assert.Equal(WindowsHideMode.HiddenAndSystem, loaded.WindowsHideMode);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsWindowState()
    {
        var store = new JsonStore<AppState>(AppState.FileName, "state", FormatVersions.State);
        store.Save(new AppState
        {
            WindowPositionX = -1200,
            WindowPositionY = 80,
            WindowWidth = 1100.5,
            WindowHeight = 720.25,
            WindowMaximized = true,
        });

        var loaded = store.Load().Value;

        Assert.Equal(-1200, loaded.WindowPositionX);
        Assert.Equal(80, loaded.WindowPositionY);
        Assert.Equal(1100.5, loaded.WindowWidth);
        Assert.Equal(720.25, loaded.WindowHeight);
        Assert.True(loaded.WindowMaximized);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefault()
    {
        var store = new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings);

        var loaded = store.Load().Value;

        Assert.Equal(WindowsHideMode.HiddenOnly, loaded.WindowsHideMode);
        Assert.False(File.Exists(PathOf("config.json")));
    }

    [Fact]
    public void Load_CorruptConfig_GivesDefaultsAndLeavesTheFileByteIdentical()
    {
        // config.json holds harmless preferences: the session runs on defaults, and nothing is moved
        // aside, reset over or left beside it until the next save replaces the file.
        var store = SettingsDocumentStore();
        store.Save(new TestDocument { WindowsHideMode = WindowsHideMode.HiddenAndSystem });

        const string corrupt = "{ not valid json";
        File.WriteAllText(PathOf("config.json"), corrupt);

        var loaded = store.Load();

        Assert.True(loaded.WasUnreadable);
        Assert.Equal(WindowsHideMode.HiddenOnly, loaded.Value.WindowsHideMode);
        Assert.Equal(corrupt, File.ReadAllText(PathOf("config.json")));
        Assert.Equal(["config.json"], Directory.EnumerateFiles(_root, "config*").Select(Path.GetFileName));
    }

    [Fact]
    public void Load_NewerConfig_GivesDefaultsAndTheNextSaveReplacesIt()
    {
        const string newer = "{\"formatVersion\":2,\"theme\":\"dark\"}";
        File.WriteAllText(PathOf("config.json"), newer);
        var store = SettingsDocumentStore();

        var loaded = store.Load();

        Assert.True(loaded.WasUnreadable);
        Assert.Equal(ThemePreference.System, loaded.Value.Theme);
        Assert.Equal(newer, File.ReadAllText(PathOf("config.json")));

        store.Save(new TestDocument { Theme = ThemePreference.Light });

        Assert.Equal(ThemePreference.Light, store.Load().Value.Theme);
        Assert.Contains("\"formatVersion\": 1", File.ReadAllText(PathOf("config.json")));
    }

    [Fact]
    public void Save_AfterAnUnusableLoad_ReplacesInvalidContent()
    {
        var store = SettingsDocumentStore();
        File.WriteAllText(PathOf("config.json"), "{ not valid json");
        store.Load();

        store.Save(new TestDocument { Theme = ThemePreference.Dark });

        Assert.Equal(ThemePreference.Dark, store.Load().Value.Theme);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.invalid"));
    }

    [MacOnlyFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Save_InaccessibleState_ReplacesItWhenTheDirectoryIsWritable()
    {
        var path = PathOf("state.json");
        File.WriteAllText(path, "{\"formatVersion\":1,\"windowWidth\":700}");
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        var store = new JsonStore<AppState>("state.json", "state", FormatVersions.State, recordBackup: false);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllBytes(path));
            Assert.True(store.Load().WasUnreadable);
            store.Save(new AppState { WindowWidth = 900 });
        }
        finally
        {
            File.SetUnixFileMode(path, mode);
        }
        Assert.Equal(900, store.Load().Value.WindowWidth);
    }

    [MacOnlyFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Load_InaccessibleConfig_GivesDefaultsAndLeavesTheFileAlone()
    {
        var path = PathOf("config.json");
        File.WriteAllText(path, "{\"formatVersion\":1,\"theme\":\"dark\"}");
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var loaded = SettingsDocumentStore().Load();

            Assert.True(loaded.WasUnreadable);
            Assert.Equal(ThemePreference.System, loaded.Value.Theme);
        }
        finally
        {
            File.SetUnixFileMode(path, mode);
        }
        Assert.Contains("dark", File.ReadAllText(path));
    }

    [MacOnlyFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Load_InaccessiblePathList_HaltsAsAnAccessFailureAndLeavesTheFileAlone()
    {
        var path = PathOf("paths.json");
        const string content = "{\"formatVersion\":1,\"paths\":[]}";
        File.WriteAllText(path, content);
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        UnreadableStoreException refused;
        try
        {
            refused = Assert.Throws<UnreadableStoreException>(() => new PathListStore().Load());
        }
        finally
        {
            File.SetUnixFileMode(path, mode);
        }

        Assert.True(refused.IsAccessFailure);
        Assert.Equal(path, refused.Path);
        Assert.Equal(content, File.ReadAllText(path));
    }

    // A folder that cannot be searched hides whether the list is there: that is a failure to read it,
    // never a first run whose empty list the next save would write over the real one.
    [MacOnlyFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Load_PathListThatCannotBeLookedAt_HaltsRatherThanStartingEmpty()
    {
        var path = PathOf("paths.json");
        const string content = "{\"formatVersion\":1,\"paths\":[{\"path\":\"/a\",\"desiredVisibility\":\"hidden\"}]}";
        File.WriteAllText(path, content);
        var mode = File.GetUnixFileMode(_root);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        UnreadableStoreException refused;
        try
        {
            Assert.False(File.Exists(path));
            refused = Assert.Throws<UnreadableStoreException>(() => new PathListStore().Load());
        }
        finally
        {
            File.SetUnixFileMode(_root, mode);
        }

        Assert.True(refused.IsAccessFailure);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Load_LiteralNullPathList_IsLeftInPlaceAndRefused()
    {
        File.WriteAllText(PathOf("paths.json"), "null");
        var store = new PathListStore();

        Assert.Throws<UnreadableStoreException>(() => store.Load());
        Assert.Throws<UnreadableStoreException>(() => store.Save([new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }]));

        Assert.Equal("null", File.ReadAllText(PathOf("paths.json")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.invalid"));
    }

    [Fact]
    public void Load_InvalidPathList_HaltsAsInvalidContent()
    {
        File.WriteAllText(PathOf("paths.json"), "{ not json");

        var refused = Assert.Throws<UnreadableStoreException>(() => new PathListStore().Load());

        Assert.False(refused.IsAccessFailure);
    }

    [Fact]
    public void Load_UnreadableStateResetsAndLeavesTheFileForTheNextSave()
    {
        File.WriteAllText(PathOf(AppState.FileName), "{ not json");
        var store = new JsonStore<AppState>(AppState.FileName, "state", FormatVersions.State, recordBackup: false);

        var loaded = store.Load();

        Assert.True(loaded.WasUnreadable);
        Assert.Null(loaded.Value.WindowPositionX);
        Assert.Equal("{ not json", File.ReadAllText(PathOf(AppState.FileName)));

        store.Save(new AppState { WindowWidth = 900 });

        Assert.Equal(900, store.Load().Value.WindowWidth);
    }

    [Fact]
    public void Load_NewerStateResetsInsteadOfHalting()
    {
        File.WriteAllText(PathOf(AppState.FileName), "{\"formatVersion\":2}");

        var loaded = new JsonStore<AppState>(AppState.FileName, "state", FormatVersions.State, recordBackup: false).Load();

        Assert.True(loaded.WasUnreadable);
        Assert.Null(loaded.Value.WindowPositionX);
    }

    [Fact]
    public void Save_FirstTime_CreatesLiveFileAndNoBak()
    {
        var store = new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings);

        store.Save(new TestDocument());

        // A first save writes exactly the live document.
        Assert.Equal(["config.json"], DocumentFiles());
    }

    [Fact]
    public void Save_IdenticalBytesKeepsModifiedTimeAfterMarkerAdmission()
    {
        var store = SettingsDocumentStore();
        var value = new TestDocument { Theme = ThemePreference.Light };
        store.Save(value);
        var path = PathOf("config.json");
        var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, timestamp);
        var before = File.ReadAllBytes(path);
        store.Save(value);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [MacOnlyFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Save_Changed_KeepsTheFilesMode_ButTakesAFreshModifiedTime()
    {
        var store = SettingsDocumentStore();
        store.Save(new TestDocument { Theme = ThemePreference.Light });
        var path = PathOf("config.json");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, mode);
        var old = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, old);

        store.Save(new TestDocument { Theme = ThemePreference.Dark });

        Assert.Equal(ThemePreference.Dark, store.Load().Value.Theme);
        Assert.Equal(mode, File.GetUnixFileMode(path));
        Assert.NotEqual(old, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Save_SecondTime_ReplacesLiveFileAndWritesNoBak()
    {
        var store = new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings);
        store.Save(new TestDocument { WindowsHideMode = WindowsHideMode.HiddenOnly });
        store.Save(new TestDocument { WindowsHideMode = WindowsHideMode.HiddenAndSystem });

        var liveJson = File.ReadAllText(PathOf("config.json"));

        // The second save atomically replaces the live file with no copy left beside it.
        Assert.Contains("hidden_and_system", liveJson);
        Assert.Equal(["config.json"], DocumentFiles());
    }

    [Fact]
    public void Save_LeavesNoTempFiles()
    {
        var store = new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings);
        store.Save(new TestDocument());
        store.Save(new TestDocument { WindowsHideMode = WindowsHideMode.HiddenAndSystem });

        var temps = Directory.EnumerateFiles(_root, "*.tmp").ToList();

        Assert.Empty(temps);
    }

    // A save cut off between writing its scratch file and renaming it leaves that file behind; the next
    // startup removes it, and nothing else in the folder.
    [Fact]
    public void Startup_removes_scratch_an_interrupted_save_left_and_nothing_else()
    {
        var left = JsonStore<AppState>.TempPath(PathOf("paths.json"), StoreScratch.NewDiscriminator());
        File.WriteAllText(left, "{\"formatVersion\":1,\"paths\":[]}");
        File.WriteAllText(PathOf("notes.tmp"), "the user's own");
        File.WriteAllText(PathOf("paths.json"), "{\"formatVersion\":1,\"paths\":[]}");

        App.ReadStartupStores();

        Assert.False(File.Exists(left));
        Assert.True(File.Exists(PathOf("notes.tmp")));
        Assert.True(File.Exists(PathOf("paths.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Successful_publication_never_enters_scratch_cleanup(bool replace)
    {
        if (replace)
            new JsonStore<AppState>("state.json", "state", FormatVersions.State, recordBackup: false)
                .Save(new AppState { WindowWidth = 700 });
        var store = new JsonStore<AppState>("state.json", "state", FormatVersions.State, recordBackup: false)
        {
            DeleteScratch = _ => throw new IOException("scratch cleanup must not delay published success"),
        };

        store.Save(new AppState { WindowWidth = 900 });

        Assert.Equal(900, store.Load().Value.WindowWidth);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Failed_publication_preserves_its_error_when_scratch_cleanup_also_fails()
    {
        Directory.CreateDirectory(PathOf("state.json")); // Rename cannot replace this directory.
        var cleanupAttempts = 0;
        var store = new JsonStore<AppState>("state.json", "state", FormatVersions.State, recordBackup: false)
        {
            DeleteScratch = _ =>
            {
                cleanupAttempts++;
                throw new IOException("cleanup failure sentinel");
            },
        };

        var failure = Assert.Throws<IOException>(() => store.Save(new AppState { WindowWidth = 900 }));

        Assert.DoesNotContain("cleanup failure sentinel", failure.Message);
        Assert.Equal(1, cleanupAttempts);
        Assert.True(Directory.Exists(PathOf("state.json")));
    }

    [Fact]
    public void TempPath_IsStemHyphenDiscriminatorDotTmp_InTheSameDirectory()
    {
        // Derived-filename grammar: <stem>-<discriminator>.tmp, one role extension, never a
        // dot-appended suffix like "config.json.<x>.tmp".
        var targetPath = PathOf("config.json");

        var tempPath = JsonStore<TestDocument>.TempPath(targetPath, "abc123");

        Assert.Equal(PathOf("config-abc123.tmp"), tempPath);
    }

    [Fact]
    public void Save_WritesCamelCasePropertiesAndSnakeCaseEnums()
    {
        var store = new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings);
        store.Save(new TestDocument { WindowsHideMode = WindowsHideMode.HiddenAndSystem });

        var json = File.ReadAllText(PathOf("config.json"));

        // Locks the on-disk shape so a serializer-option change can't silently
        // orphan existing user files.
        Assert.Contains("\"windowsHideMode\"", json);
        Assert.Contains("\"hidden_and_system\"", json);
    }

    [Fact]
    public void SettingsAndPaths_ResolveToDistinctFiles()
    {
        // The durable settings live in config.json; the user's path list lives in
        // paths.json. They are separate roles and must never collapse onto one file.
        // This guards the settings-file rename.
        var settingsStore = new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings);
        var pathListStore = new PathListStore();

        settingsStore.Save(new TestDocument { WindowsHideMode = WindowsHideMode.HiddenAndSystem });
        pathListStore.Save([new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }]);

        // Each store writes its own document and nothing beside it.
        Assert.Equal(["config.json", "paths.json"], DocumentFiles());

        // Each store round-trips only its own document; the roles do not bleed together.
        Assert.Equal(WindowsHideMode.HiddenAndSystem, settingsStore.Load().Value.WindowsHideMode);
        Assert.Single(pathListStore.Load().Value);
    }

    // --- Write-through backup history: the record fires from this one choke point, after the rename,
    // for both managed files, byte-identically to what landed on disk, keyed by the final path. ---

    // The files a save can leave in the storage root, apart from the history and records databases.
    private string[] DocumentFiles() =>
        Directory.GetFiles(_root)
            .Select(Path.GetFileName)
            .Where(name => !name!.EndsWith(".sqlite3", StringComparison.Ordinal) && !name.Contains(".sqlite3-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray()!;

    /// <summary>Reads the recorded content blob(s) for a path from the throwaway root's backups.sqlite3,
    /// opening a private read-only connection once the pending writes have landed.</summary>
    private List<byte[]> RecordedContentsFor(string absolutePath)
    {
        BackupStore.Close();
        var contents = new List<byte[]>();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = PathOf(BackupStore.FileName),
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
        BackupStore.Close();
        var paths = new List<string>();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = PathOf(BackupStore.FileName),
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
        var store = new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings);
        store.Save(new TestDocument { WindowsHideMode = WindowsHideMode.HiddenAndSystem });

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
        new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings).Save(new TestDocument());
        new PathListStore()
            .Save([new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }]);

        Assert.Single(RecordedContentsFor(PathOf("config.json")));
        Assert.Single(RecordedContentsFor(PathOf("paths.json")));
    }

    [Fact]
    public void Save_WithRecordBackupFalse_WritesTheFile_ButRecordsNothing()
    {
        // Volatile state (window geometry) is written atomically like any store but never recorded.
        var stateStore = new JsonStore<AppState>("state.json", "state", FormatVersions.State, recordBackup: false);
        stateStore.Save(new AppState());
        new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings).Save(new TestDocument());

        Assert.True(File.Exists(PathOf("state.json")));
        Assert.Empty(RecordedContentsFor(PathOf("state.json")));
        Assert.Equal(new[] { PathOf("config.json") }, RecordedPaths());
    }

    [Fact]
    public void Save_ChangedResaveInOneSession_KeepsOneRowHoldingTheLastVersion()
    {
        var store = new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings);
        store.Save(new TestDocument { WindowsHideMode = WindowsHideMode.HiddenOnly });
        store.Save(new TestDocument { WindowsHideMode = WindowsHideMode.HiddenAndSystem });

        Assert.Equal(File.ReadAllBytes(PathOf("config.json")), Assert.Single(RecordedContentsFor(PathOf("config.json"))));
    }

    [Fact]
    public void Save_FailedRecord_NeverBreaksTheSave()
    {
        // Close the store, then stand a *file* where its backups.sqlite3 belongs so the next open fails.
        // The save must still fully succeed (file on disk, round-trips) — the backup is best-effort and can
        // never break a save that already landed.
        BackupStore.Close();
        File.WriteAllText(PathOf(BackupStore.FileName), "not a database");

        var store = new JsonStore<TestDocument>("config.json", "settings", FormatVersions.Settings);
        var exception = Xunit.Record.Exception(() =>
            store.Save(new TestDocument { WindowsHideMode = WindowsHideMode.HiddenAndSystem }));

        Assert.Null(exception);
        Assert.True(File.Exists(PathOf("config.json")));
        Assert.Equal(WindowsHideMode.HiddenAndSystem, store.Load().Value.WindowsHideMode);
    }
}
