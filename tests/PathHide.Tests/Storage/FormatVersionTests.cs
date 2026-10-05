using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PathHide.Backup;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>
/// Each store's format version (store-recovery-conventions): a store without its marker is unreadable,
/// the current version round-trips, and a newer one is refused and left byte-identical.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class FormatVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pathhide-tests", NanoId.New());
    private readonly string? _previousDataDir;

    public FormatVersionTests()
    {
        Directory.CreateDirectory(_root);
        _previousDataDir = Environment.GetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _root);
        BackupStore.Close();
        QuarantineJournal.Drain();
    }

    public void Dispose()
    {
        BackupStore.Close();
        QuarantineJournal.Drain();
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _previousDataDir);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string PathOf(string fileName) => Path.Combine(_root, fileName);

    private int RecordedIn(string fileName) =>
        JsonNode.Parse(File.ReadAllText(PathOf(fileName)))![FormatVersions.JsonKey]!.GetValue<int>();

    private static JsonStore<AppState> StateStore() =>
        new(AppState.FileName, QuarantineJournal.StateLabel, FormatVersions.State, recordBackup: false);

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
        Assert.Empty(QuarantineJournal.Drain());
    }

    // --- paths.json ---

    [Fact]
    public void PathList_WithoutAVersion_IsUnreadable()
    {
        const string contents = """{ "paths": [ { "path": "/a", "desiredVisibility": "hidden" } ] }""";
        File.WriteAllText(PathOf(PathListStore.FileName), contents);

        var loaded = new PathListStore().Load();

        Assert.True(loaded.WasUnreadable);
        Assert.Equal(contents, File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "paths-*.invalid"))));
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
    [InlineData("""[ { "path": "/a", "desiredVisibility": "hidden" } ]""")]
    [InlineData("""{ "formatVersion": 0, "paths": [] }""")]
    [InlineData("""{ "formatVersion": "1", "paths": [] }""")]
    [InlineData("""{ "formatVersion": 1.5, "paths": [] }""")]
    [InlineData("""{ "formatVersion": null, "paths": [] }""")]
    [InlineData("""{ "formatVersion": 1, "paths": null }""")]
    public void PathList_WithoutAReadableVersionOrShape_IsQuarantined(string contents)
    {
        File.WriteAllText(PathOf(PathListStore.FileName), contents);

        var loaded = new PathListStore().Load();

        Assert.True(loaded.WasUnreadable);
        Assert.Equal(contents, File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "paths-*.invalid"))));
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
        Assert.Equal(contents, File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "config-*.invalid"))));
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
    public void Settings_Newer_AreRefusedAndLeftByteIdentical()
    {
        var bytes = WriteNewer(AppSettings.FileName, """{ "formatVersion": 2, "language": "ja", "theme": "dark" }""");
        var store = new SettingsStore();

        Assert.Throws<NewerFormatException>(() => store.Load());
        Assert.Throws<NewerFormatException>(() =>
            store.SaveChanges(new AppSettings(), new AppSettings { Theme = ThemePreference.Light }));

        // The language is not read out of a file this build cannot read.
        Assert.Equal(Languages.System, LanguageBootstrap.SavedPreference());
        AssertLeftAsItWas(AppSettings.FileName, bytes);
    }

    // --- state.json ---

    [Fact]
    public void State_WithoutAVersion_IsUnreadable()
    {
        File.WriteAllText(PathOf(AppState.FileName), """{ "windowWidth": 900 }""");

        var loaded = StateStore().Load();

        Assert.True(loaded.WasUnreadable);
        Assert.Null(loaded.Value.WindowWidth);
        Assert.Single(Directory.GetFiles(_root, "state-*.invalid"));
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
    public void State_Newer_IsRefusedAndLeftByteIdentical()
    {
        var bytes = WriteNewer(AppState.FileName, """{ "formatVersion": 2, "windows": [] }""");
        var store = StateStore();

        Assert.Throws<NewerFormatException>(() => store.Load());
        Assert.Throws<NewerFormatException>(() => store.Save(new AppState { WindowWidth = 900 }));

        AssertLeftAsItWas(AppState.FileName, bytes);
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

    // --- records.sqlite3 ---

    [Fact]
    public async Task Records_WithoutAVersion_AreUnreadableAndLeftByteIdentical()
    {
        var path = PathOf(RecordsStore.FileName);
        CreateDatabase(path, userVersion: 0);
        var bytes = File.ReadAllBytes(path);

        var store = RecordsStore.TryOpen(path, out var failure);
        await Assert.ThrowsAsync<InvalidDataException>(() => new RecordsReader(path).ReadSessionsAsync());

        Assert.Null(store);
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(bytes, File.ReadAllBytes(path));
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

    [Fact]
    public void Backups_WithoutAVersion_AreUnreadableAndLeftByteIdentical()
    {
        CreateDatabase(BackupsPath, userVersion: 0);
        var bytes = File.ReadAllBytes(BackupsPath);

        BackupStore.Record(PathOf(AppSettings.FileName), Encoding.UTF8.GetBytes("{}"));
        BackupStore.Close();

        Assert.Equal(bytes, File.ReadAllBytes(BackupsPath));
    }

    [Fact]
    public void Backups_RoundTripAtTheirVersion()
    {
        BackupStore.Record(PathOf(AppSettings.FileName), Encoding.UTF8.GetBytes("{}"));
        BackupStore.Close();
        BackupStore.Record(PathOf(AppSettings.FileName), Encoding.UTF8.GetBytes("{ }"));
        BackupStore.Close();

        Assert.Equal(FormatVersions.Backups, UserVersion(BackupsPath));
        Assert.Equal(2, BackupRows());
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
