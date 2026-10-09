using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PathHide.Backup;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>
/// Each store's format version (store-recovery-conventions): a store without its marker is unreadable,
/// the current version round-trips, and a newer one is never read. The path list and the databases
/// refuse it and leave it byte-identical; settings and window state run on defaults and leave it
/// untouched until their next save replaces it.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class FormatVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pathhide-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _previousDataDir;

    public FormatVersionTests()
    {
        Directory.CreateDirectory(_root);
        _previousDataDir = Environment.GetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _root);
        BackupStore.Close();
    }

    public void Dispose()
    {
        BackupStore.Close();
        // Closing returns each connection to Microsoft.Data.Sqlite's pool, which keeps its file open;
        // Windows cannot delete an open database file.
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _previousDataDir);
        Directory.Delete(_root, recursive: true);
    }

    private string PathOf(string fileName) => Path.Combine(_root, fileName);

    private int RecordedIn(string fileName) =>
        JsonNode.Parse(File.ReadAllText(PathOf(fileName)))![FormatVersions.JsonKey]!.GetValue<int>();

    private static JsonStore<AppState> StateStore() =>
        new(AppState.FileName, "state", FormatVersions.State, recordBackup: false);

    private static PathEntry Entry(string path) => new() { Path = path, DesiredVisibility = DesiredVisibility.Hidden };

    /// <summary>Writes <paramref name="contents"/> as a newer build would, and returns its bytes.</summary>
    private byte[] WriteNewer(string fileName, string contents)
    {
        File.WriteAllText(PathOf(fileName), contents);
        return File.ReadAllBytes(PathOf(fileName));
    }

    private void AssertLeftAsItWas(string fileName, byte[] bytes)
    {
        Assert.Equal(bytes, File.ReadAllBytes(PathOf(fileName)));
        Assert.Empty(Directory.GetFiles(_root, "*.invalid"));
    }

    // --- paths.json ---

    [Fact]
    public void PathList_WithoutAVersion_IsUnreadableAndLeftInPlace()
    {
        var bytes = WriteNewer(PathListStore.FileName, """{ "paths": [ { "path": "/a", "desiredVisibility": "hidden" } ] }""");
        var store = new PathListStore();

        var refused = Assert.Throws<UnreadableStoreException>(() => store.Load());
        Assert.Throws<UnreadableStoreException>(() => store.Save([Entry("/b")]));

        Assert.Equal(PathOf(PathListStore.FileName), refused.Path);
        AssertLeftAsItWas(PathListStore.FileName, bytes);
    }

    [Fact]
    public void PathList_RoundTripsAtItsVersion()
    {
        var store = new PathListStore();

        store.Save([Entry("/a")]);

        Assert.Equal(FormatVersions.PathList, RecordedIn(PathListStore.FileName));
        Assert.Equal("/a", Assert.Single(store.Load().Value).Path);
    }

    [Fact]
    public void PathList_Newer_IsRefusedAndLeftByteIdentical()
    {
        var bytes = WriteNewer(PathListStore.FileName, """{ "formatVersion": 2, "entries": {} }""");
        var store = new PathListStore();

        var refused = Assert.Throws<NewerFormatException>(() => store.Load());
        Assert.Throws<NewerFormatException>(() => store.Save([Entry("/a")]));

        Assert.Equal(PathOf(PathListStore.FileName), refused.Path);
        Assert.Equal(2, refused.Version);
        AssertLeftAsItWas(PathListStore.FileName, bytes);
    }

    [Theory]
    [InlineData("""{ "formatVersion": 1 }""")]
    [InlineData("""{ "formatVersion": 1, "paths": [ { "path": "/a/", "desiredVisibility": "hidden" }, { "path": "/A", "desiredVisibility": "shown" } ] }""")]
    [InlineData("""{ "formatVersion": 1, "paths": [ { "path": "C:/a", "desiredVisibility": "hidden" }, { "path": "c:\\A\\", "desiredVisibility": "shown" } ] }""")]
    [InlineData("""[ { "path": "/a", "desiredVisibility": "hidden" } ]""")]
    [InlineData("""{ "formatVersion": 0, "paths": [] }""")]
    [InlineData("""{ "formatVersion": "1", "paths": [] }""")]
    [InlineData("""{ "formatVersion": 1.5, "paths": [] }""")]
    [InlineData("""{ "formatVersion": null, "paths": [] }""")]
    [InlineData("""{ "formatVersion": 1, "paths": null }""")]
    [InlineData("""{ "formatVersion": 1, "paths": [ null ] }""")]
    [InlineData("""{ "formatVersion": 1, "paths": [ { "path": null, "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "formatVersion": 1, "paths": [ { "path": "", "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "formatVersion": 1, "paths": [ { "path": "relative/a", "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "formatVersion": 1, "paths": [ { "path": "/a", "desiredVisibility": 2 } ] }""")]
    public void PathList_WithoutAReadableVersionOrShape_IsLeftInPlace(string contents)
    {
        var bytes = WriteNewer(PathListStore.FileName, contents);
        var store = new PathListStore();

        Assert.Throws<UnreadableStoreException>(() => store.Load());
        Assert.Throws<UnreadableStoreException>(() => store.Save([Entry("/b")]));

        AssertLeftAsItWas(PathListStore.FileName, bytes);
    }

    [Fact]
    public void PathList_AcceptsEveryPathFamilyOnEveryPlatform()
    {
        File.WriteAllText(PathOf(PathListStore.FileName), """
            { "formatVersion": 1, "paths": [
              { "path": "/a", "desiredVisibility": "hidden" },
              { "path": "C:\\a", "desiredVisibility": "shown" },
              { "path": "\\\\server\\share\\a", "desiredVisibility": 0 } ] }
            """);

        var loaded = new PathListStore().Load().Value;

        Assert.Equal(["/a", @"C:\a", @"\\server\share\a"], loaded.Select(entry => entry.Path));
        Assert.Equal([DesiredVisibility.Hidden, DesiredVisibility.Shown, DesiredVisibility.Hidden],
            loaded.Select(entry => entry.DesiredVisibility));
    }

    [Theory]
    [InlineData("{ not valid json")]
    [InlineData("""{ "formatVersion": 1, "paths": [ { "path": "/a", "desiredVisibility": "hidden" }, null ] }""")]
    [InlineData("""{ "formatVersion": 1, "paths": [ { "path": "relative/a", "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "formatVersion": 1, "paths": [ { "path": "/a", "desiredVisibility": 7 } ] }""")]
    public void PathList_Unreadable_HaltsEveryLaunchWithoutTouchingTheFileOrAnyItem(string contents)
    {
        var bytes = WriteNewer(PathListStore.FileName, contents);
        var visibility = new FakeVisibilityService();

        // Each launch builds a fresh view model over the real store; none may start, and none may
        // move, rewrite or act on anything.
        for (var launch = 0; launch < 2; launch++)
        {
            var settingsStore = new FakeSettingsStore();
            var vm = new MainWindowViewModel(new BoundedVisibility(visibility), new PathListStore(), settingsStore,
                settingsStore.Load().Value, new FakeJsonStore<AppState>(), new AppState());
            var halted = Assert.Throws<UnreadableStoreException>(vm.LoadPersistedState);
            Assert.Equal(PathOf(PathListStore.FileName), halted.Path);
            Assert.Empty(vm.Rows);
        }

        AssertLeftAsItWas(PathListStore.FileName, bytes);
        Assert.Empty(visibility.Inspected);
        Assert.Empty(visibility.Hidden);
        Assert.Empty(visibility.Shown);
    }

    // --- config.json ---

    [Fact]
    public void Settings_WithoutAVersion_AreUnreadable()
    {
        const string contents = """{ "language": "ja", "theme": "dark" }""";
        File.WriteAllText(PathOf(AppSettings.FileName), contents);

        Assert.Equal(Languages.System, LanguageBootstrap.SavedPreference());
        var loaded = new SettingsStore().Load();

        Assert.True(loaded.WasUnreadable);
        Assert.Equal(ThemePreference.System, loaded.Value.Theme);
        AssertLeftAsItWas(AppSettings.FileName, Encoding.UTF8.GetBytes(contents));
    }

    [Fact]
    public void Settings_RoundTripAtTheirVersion()
    {
        var store = new SettingsStore();

        Assert.True(store.SaveChanges(new AppSettings(), new AppSettings { Language = "ja", Theme = ThemePreference.Dark }));

        Assert.Equal(FormatVersions.Settings, RecordedIn(AppSettings.FileName));
        Assert.Equal(ThemePreference.Dark, store.Load().Value.Theme);
        Assert.Equal("ja", LanguageBootstrap.SavedPreference());
    }

    [Fact]
    public void Settings_Newer_LoadBuiltIns_StayUntouched_UntilTheNextSaveReplacesThem()
    {
        var bytes = WriteNewer(AppSettings.FileName, """{ "formatVersion": 2, "language": "ja", "theme": "dark" }""");
        var store = new SettingsStore();

        var loaded = store.Load();

        Assert.True(loaded.WasUnreadable);
        Assert.Equal(ThemePreference.System, loaded.Value.Theme);
        // The language is not read out of a file this build cannot read.
        Assert.Equal(Languages.System, LanguageBootstrap.SavedPreference());
        AssertLeftAsItWas(AppSettings.FileName, bytes);

        Assert.True(store.SaveChanges(loaded.Value, new AppSettings { Theme = ThemePreference.Light }));

        Assert.Equal(FormatVersions.Settings, RecordedIn(AppSettings.FileName));
        Assert.Equal(ThemePreference.Light, store.Load().Value.Theme);
    }

    // --- state.json ---

    [Fact]
    public void State_WithoutAVersion_IsUnreadable()
    {
        File.WriteAllText(PathOf(AppState.FileName), """{ "windowWidth": 900 }""");

        var loaded = StateStore().Load();

        Assert.True(loaded.WasUnreadable);
        Assert.Null(loaded.Value.WindowWidth);
        AssertLeftAsItWas(AppState.FileName, Encoding.UTF8.GetBytes("""{ "windowWidth": 900 }"""));
    }

    [Fact]
    public void State_RoundTripsAtItsVersion()
    {
        var store = StateStore();

        store.Save(new AppState { WindowWidth = 900 });

        Assert.Equal(FormatVersions.State, RecordedIn(AppState.FileName));
        Assert.Equal(900, store.Load().Value.WindowWidth);
    }

    [Fact]
    public void State_Newer_ResetsAndTheNextSaveReplacesIt()
    {
        var bytes = WriteNewer(AppState.FileName, """{ "formatVersion": 2, "windows": [] }""");
        var store = StateStore();

        Assert.True(store.Load().WasUnreadable);
        AssertLeftAsItWas(AppState.FileName, bytes);

        store.Save(new AppState { WindowWidth = 900 });

        Assert.Equal(900, store.Load().Value.WindowWidth);
    }

    // --- SQLite ---

    private static string ConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Pooling = false,
    }.ToString();

    /// <summary>A database holding one table and recording <paramref name="userVersion"/>; 0 is no marker.</summary>
    private static void CreateDatabase(string path, int userVersion)
    {
        using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadWriteCreate));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE earlier (x); PRAGMA user_version = {userVersion};";
        command.ExecuteNonQuery();
    }

    private static long UserVersion(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void AnEmptyFileHoldsNothing_SoItIsInitializedAsNew()
    {
        var path = PathOf(RecordsStore.FileName);
        File.WriteAllBytes(path, []);

        using (var store = RecordsStore.TryOpen(path, out var failure) ?? throw failure!)
            Assert.Equal(FormatVersions.Records, UserVersion(path));
    }

    [Fact]
    public void FailedInitializationLeavesNoPartialSchema_AndTheNextOpenInitializes()
    {
        var path = PathOf("failed.sqlite3");
        Assert.Throws<SqliteException>(() => FormatVersions.OpenDatabase(path, 1,
            "CREATE TABLE first (x); INVALID SQL;"));
        Assert.Equal(0, UserVersion(path));

        using (FormatVersions.OpenDatabase(path, 1, "CREATE TABLE IF NOT EXISTS first (x);"))
        {
        }
        Assert.Equal(1, UserVersion(path));
    }

    // --- records.sqlite3 ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Records_WithoutAVersion_AreUnreadableAndLeftByteIdentical(int version)
    {
        var path = PathOf(RecordsStore.FileName);
        CreateDatabase(path, userVersion: version);
        var bytes = File.ReadAllBytes(path);

        var store = RecordsStore.TryOpen(path, out var failure);
        await Assert.ThrowsAsync<InvalidDataException>(() => new RecordsReader(path).ReadSessionsAsync());

        Assert.Null(store);
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Records_CompatibleMarkerCompletesMissingSchema()
    {
        var path = PathOf(RecordsStore.FileName);
        CreateDatabase(path, FormatVersions.Records);
        using (var store = RecordsStore.TryOpen(path, out var failure) ?? throw failure!)
            store.Write(new LogEntry("2026-10-05T00:00:00.000Z", "2026-10-05T00:00:00.000Z", "info", "kept", null, null));
        Assert.Equal(FormatVersions.Records, UserVersion(path));
        Assert.Equal("kept", Assert.Single((await new RecordsReader(path).ReadPageAsync(new RecordsQuery(null, null, "", null))).Records).Message);
    }

    [Fact]
    public async Task Records_RoundTripAtTheirVersion()
    {
        var path = PathOf(RecordsStore.FileName);
        using (var store = RecordsStore.TryOpen(path, out var failure) ?? throw failure!)
            store.Write(new PathHide.Services.LogEntry("2026-10-05T00:00:00.000Z", "2026-10-05T00:00:00.000Z", "info", "kept", null, null));

        Assert.Equal(FormatVersions.Records, UserVersion(path));
        Assert.Equal("kept", Assert.Single((await new RecordsReader(path).ReadPageAsync(new RecordsQuery(null, null, "", null))).Records).Message);
    }

    [Fact]
    public async Task Records_Newer_AreRefusedAndLeftByteIdentical()
    {
        var path = PathOf(RecordsStore.FileName);
        CreateDatabase(path, userVersion: 2);
        var bytes = File.ReadAllBytes(path);

        var store = RecordsStore.TryOpen(path, out var failure);
        await Assert.ThrowsAsync<NewerFormatException>(() => new RecordsReader(path).ReadSessionsAsync());

        Assert.Null(store);
        Assert.Equal(2, Assert.IsType<NewerFormatException>(failure).Version);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    // --- backups.sqlite3 ---

    private string BackupsPath => PathOf("backups.sqlite3");

    private long BackupRows()
    {
        using var connection = new SqliteConnection(ConnectionString(BackupsPath, SqliteOpenMode.ReadOnly));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM backups;";
        return (long)command.ExecuteScalar()!;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Backups_WithoutAVersion_AreUnreadableAndLeftByteIdentical(int version)
    {
        CreateDatabase(BackupsPath, userVersion: version);
        var bytes = File.ReadAllBytes(BackupsPath);

        BackupStore.Record(PathOf(AppSettings.FileName), Encoding.UTF8.GetBytes("{}"));
        BackupStore.Close();

        Assert.Equal(bytes, File.ReadAllBytes(BackupsPath));
    }

    [Fact]
    public void Backups_CompatibleMarkerCompletesMissingSchema()
    {
        CreateDatabase(BackupsPath, FormatVersions.Backups);
        BackupStore.Record(PathOf(AppSettings.FileName), Encoding.UTF8.GetBytes("{}"));
        BackupStore.Close();
        Assert.Equal(FormatVersions.Backups, UserVersion(BackupsPath));
        Assert.Equal(1, BackupRows());
    }

    [Fact]
    public void Backups_RoundTripAtTheirVersion()
    {
        BackupStore.Record(PathOf(AppSettings.FileName), Encoding.UTF8.GetBytes("{}"));
        BackupStore.Close();
        BackupStore.Record(PathOf(AppSettings.FileName), Encoding.UTF8.GetBytes("{ }"));
        BackupStore.Close();

        Assert.Equal(FormatVersions.Backups, UserVersion(BackupsPath));
        Assert.Equal(1, BackupRows());
    }

    [Fact]
    public void Backups_Newer_AreRefusedAndLeftByteIdentical()
    {
        CreateDatabase(BackupsPath, userVersion: 2);
        var bytes = File.ReadAllBytes(BackupsPath);

        BackupStore.Record(PathOf(AppSettings.FileName), Encoding.UTF8.GetBytes("{}"));
        BackupStore.Close();

        Assert.Equal(bytes, File.ReadAllBytes(BackupsPath));
    }
}
