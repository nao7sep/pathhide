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
using Avalonia.Headless.XUnit;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>
/// Each store reads only its own format, by the identity header alone. A newer version is refused and
/// left byte-identical; a missing or malformed header, another owner or kind, or any other version makes
/// the store unreadable like any damaged file. The writer is never read. Harmless settings and window
/// state use session defaults but refuse overwriting newer formats.
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class FileHeaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pathhide-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _previousDataDir;

    public FileHeaderTests()
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

    /// <summary>Asserts the file opens with the header naming <paramref name="format"/> and this build as its writer.</summary>
    private void AssertHeaderIn(string fileName, FileFormat format)
    {
        var root = JsonNode.Parse(File.ReadAllText(PathOf(fileName)))!.AsObject();
        Assert.Equal([FileFormats.FormatKey, FileFormats.WriterKey], root.Select(property => property.Key).Take(2));
        Assert.Equal(FileFormats.Owner, (string?)root["format"]!["owner"]);
        Assert.Equal(format.Kind, (string?)root["format"]!["kind"]);
        Assert.Equal(format.Version, (int)root["format"]!["version"]!);
        Assert.Equal("pathhide", (string?)root["writer"]!["name"]);
        Assert.Equal("1.0.0", (string?)root["writer"]!["version"]);
        Assert.Equal(2, root["writer"]!.AsObject().Count);
    }

    private static JsonStore<AppState> StateStore() =>
        new(AppState.FileName, "state", FileFormats.State, recordBackup: false);

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

    [Theory]
    // No header at all, and the marker earlier builds wrote, which no reader accepts any more.
    [InlineData("""{ "paths": [ { "path": "/a", "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "formatVersion": 1, "paths": [] }""")]
    // A malformed header.
    [InlineData("""{ "format": null, "paths": [] }""")]
    [InlineData("""{ "format": 1, "paths": [] }""")]
    [InlineData("""{ "format": "pathhide.pathList", "paths": [] }""")]
    [InlineData("""{ "format": { "kind": "path-list", "version": 1 }, "paths": [] }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "version": 1 }, "paths": [] }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "path-list" }, "paths": [] }""")]
    [InlineData("""{ "format": { "owner": 1, "kind": "path-list", "version": 1 }, "paths": [] }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "path-list", "version": null }, "paths": [] }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "path-list", "version": "1" }, "paths": [] }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "path-list", "version": 1.5 }, "paths": [] }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "path-list", "version": 0 }, "paths": [] }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "path-list", "version": -1 }, "paths": [] }""")]
    // Another owner or kind, whatever the shape: another app's list, or PathHide's settings.
    [InlineData("""{ "format": { "owner": "scriptdock", "kind": "path-list", "version": 1 }, "paths": [] }""")]
    [InlineData("""{ "format": { "owner": "PathHide", "kind": "path-list", "version": 1 }, "paths": [] }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "config", "version": 1 }, "paths": [] }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "pathList", "version": 1 }, "paths": [] }""")]
    public void PathList_WithoutItsHeader_IsUnreadableAndLeftAsItWas(string contents)
    {
        var bytes = WriteNewer(PathListStore.FileName, contents);
        var store = new PathListStore();

        Assert.Throws<UnreadableStoreException>(() => store.Load());
        Assert.Throws<UnreadableStoreException>(() => store.Save([Entry("/a")]));
        AssertLeftAsItWas(PathListStore.FileName, bytes);
    }

    [Fact]
    public void PathList_RoundTripsAtItsVersion()
    {
        var store = new PathListStore();

        store.Save([Entry("/a")]);

        AssertHeaderIn(PathListStore.FileName, FileFormats.PathList);
        Assert.Equal("/a", Assert.Single(store.Load().Value).Path);
    }

    // The writer says which build wrote the file, for people: it is never read, so a missing, odd or
    // foreign writer changes nothing, and unknown keys inside format are ignored.
    [Theory]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "path-list", "version": 1 }, "paths": [ { "path": "/a", "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "path-list", "version": 1 }, "writer": null, "paths": [ { "path": "/a", "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "path-list", "version": 1 }, "writer": { "name": "otherapp", "version": 9 }, "paths": [ { "path": "/a", "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "paths": [ { "path": "/a", "desiredVisibility": "hidden" } ], "writer": "x", "format": { "version": 1, "kind": "path-list", "owner": "pathhide", "later": true } }""")]
    public void PathList_IsReadByItsFormatAlone_NeverByItsWriter(string contents)
    {
        File.WriteAllText(PathOf(PathListStore.FileName), contents);

        Assert.Equal("/a", Assert.Single(new PathListStore().Load().Value).Path);
    }

    [Fact]
    public void PathList_Newer_IsRefusedAndLeftByteIdentical()
    {
        var bytes = WriteNewer(PathListStore.FileName, """{ "format": { "owner": "pathhide", "kind": "path-list", "version": 2 }, "entries": {} }""");
        var store = new PathListStore();

        var refused = Assert.Throws<NewerFormatException>(() => store.Load());
        Assert.Throws<NewerFormatException>(() => store.Save([Entry("/a")]));

        Assert.Equal(PathOf(PathListStore.FileName), refused.Path);
        Assert.Equal(2, refused.Version);
        AssertLeftAsItWas(PathListStore.FileName, bytes);
    }

    [Theory]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "path-list", "version": 1 } }""")]
    [InlineData("""{ "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": [ { "path": "/a/", "desiredVisibility": "hidden" }, { "path": "/a", "desiredVisibility": "shown" } ] }""")]
    [InlineData("""{ "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": [ { "path": "C:/a", "desiredVisibility": "hidden" }, { "path": "c:\\A\\", "desiredVisibility": "shown" } ] }""")]
    [InlineData("""[ { "path": "/a", "desiredVisibility": "hidden" } ]""")]
    [InlineData("""{ "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": null }""")]
    [InlineData("""{ "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": [ null ] }""")]
    [InlineData("""{ "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": [ { "path": null, "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": [ { "path": "", "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": [ { "path": "relative/a", "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": [ { "path": "/a", "desiredVisibility": 2 } ] }""")]
    public void PathList_WithoutAReadableShape_IsLeftInPlace(string contents)
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
            { "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": [
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
    [InlineData("""{ "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": [ { "path": "/a", "desiredVisibility": "hidden" }, null ] }""")]
    [InlineData("""{ "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": [ { "path": "relative/a", "desiredVisibility": "hidden" } ] }""")]
    [InlineData("""{ "format":{"owner":"pathhide","kind":"path-list","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "paths": [ { "path": "/a", "desiredVisibility": 7 } ] }""")]
    public void PathList_Unreadable_HaltsEveryLaunchWithoutTouchingTheFile(string contents)
    {
        var bytes = WriteNewer(PathListStore.FileName, contents);

        // Each launch uses the real startup gate; neither may create a shell or rewrite the list.
        for (var launch = 0; launch < 2; launch++)
        {
            var halted = Assert.Throws<StartupRefusedException>(() => App.ReadStartupStores());
            Assert.Equal(PathOf(PathListStore.FileName), Assert.Single(halted.Refusals).Path);
        }

        AssertLeftAsItWas(PathListStore.FileName, bytes);
    }

    // --- config.json ---

    [Theory]
    [InlineData("""{ "language": "ja", "theme": "dark" }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "config" }, "language": "ja", "theme": "dark" }""")]
    [InlineData("""{ "format": { "owner": "scriptdock", "kind": "config", "version": 1 }, "language": "ja", "theme": "dark" }""")]
    [InlineData("""{ "format": { "owner": "pathhide", "kind": "state", "version": 1 }, "language": "ja", "theme": "dark" }""")]
    public void Settings_WithoutTheirHeader_AreUnreadableToTheStoreAndTheLanguageRead(string contents)
    {
        File.WriteAllText(PathOf(AppSettings.FileName), contents);

        Assert.Equal(Languages.System, LanguageBootstrap.SavedPreference());
        var store = new SettingsStore();
        Assert.Throws<UnreadableStoreException>(() => store.Load());
        Assert.Throws<UnreadableStoreException>(() => store.SaveChanges(null, new AppSettings { Theme = ThemePreference.Light }));
        AssertLeftAsItWas(AppSettings.FileName, Encoding.UTF8.GetBytes(contents));
    }

    [Fact]
    public void Settings_RoundTripAtTheirVersion()
    {
        var store = new SettingsStore();

        Assert.True(store.SaveChanges(new AppSettings(), new AppSettings { Language = "ja", Theme = ThemePreference.Dark }));

        AssertHeaderIn(AppSettings.FileName, FileFormats.Settings);
        Assert.Equal(ThemePreference.Dark, store.Load().Value.Theme);
        Assert.Equal("ja", LanguageBootstrap.SavedPreference());
    }

    [Fact]
    public void Settings_WithARepeatedKey_AreUnreadableToTheStoreAndTheLanguageRead()
    {
        const string contents = """{ "format":{"owner":"pathhide","kind":"config","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "language": "ja", "language": "de" }""";
        File.WriteAllText(PathOf(AppSettings.FileName), contents);

        Assert.Throws<UnreadableStoreException>(() => new SettingsStore().Load());
        Assert.Equal(Languages.System, LanguageBootstrap.SavedPreference());
    }

    [Fact]
    public void Settings_Newer_AreRefusedByLoadAndSave()
    {
        var bytes = WriteNewer(AppSettings.FileName, """{ "format":{"owner":"pathhide","kind":"config","version":2},"writer":{"name":"pathhide","version":"1.0.0"}, "language": "ja", "theme": "dark" }""");
        var store = new SettingsStore();

        Assert.Throws<NewerFormatException>(() => store.Load());
        // The language is not read out of a file this build cannot read.
        Assert.Equal(Languages.System, LanguageBootstrap.SavedPreference());
        AssertLeftAsItWas(AppSettings.FileName, bytes);

        Assert.Throws<NewerFormatException>(() => store.SaveChanges(null, new AppSettings { Theme = ThemePreference.Light }));
        AssertLeftAsItWas(AppSettings.FileName, bytes);
    }

    // --- state.json ---

    [Fact]
    public void State_WithoutAHeader_IsUnreadable_SoItTakesDefaults()
    {
        File.WriteAllText(PathOf(AppState.FileName), """{ "windowWidth": 900 }""");

        var loaded = StateStore().Load();

        Assert.True(loaded.WasUnreadable);
        Assert.NotEqual(900, loaded.Value.WindowWidth);
        AssertLeftAsItWas(AppState.FileName, Encoding.UTF8.GetBytes("""{ "windowWidth": 900 }"""));
    }

    [Fact]
    public void State_RoundTripsAtItsVersion()
    {
        var store = StateStore();

        store.Save(new AppState { WindowWidth = 900 });

        AssertHeaderIn(AppState.FileName, FileFormats.State);
        Assert.Equal(900, store.Load().Value.WindowWidth);
    }

    [Fact]
    public void State_Newer_IsRefusedByLoadAndSave()
    {
        var bytes = WriteNewer(AppState.FileName, """{ "format":{"owner":"pathhide","kind":"state","version":2},"writer":{"name":"pathhide","version":"1.0.0"}, "windows": [] }""");
        var store = StateStore();

        Assert.Throws<NewerFormatException>(() => store.Load());
        AssertLeftAsItWas(AppState.FileName, bytes);

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

    /// <summary>
    /// A database holding one table and recording <paramref name="userVersion"/> (0 is none), with a
    /// <c>file_header</c> naming <paramref name="owner"/> and <paramref name="kind"/> unless
    /// <paramref name="kind"/> is null.
    /// </summary>
    private static void CreateDatabase(string path, int userVersion, string? kind, string owner = FileFormats.Owner)
    {
        using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadWriteCreate));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE earlier (x); PRAGMA user_version = {userVersion};";
        if (kind is not null)
            command.CommandText +=
                "CREATE TABLE file_header (key TEXT PRIMARY KEY, value TEXT NOT NULL);" +
                $"INSERT INTO file_header VALUES ('format.owner', '{owner}'), ('format.kind', '{kind}'), " +
                "('writer.name', 'otherwriter'), ('writer.version', '0.0.1');";
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

    private static Dictionary<string, string> HeaderRows(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM file_header;";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, string>();
        while (reader.Read())
            rows[reader.GetString(0)] = reader.GetString(1);
        return rows;
    }

    /// <summary>Asserts the database records <paramref name="format"/> and this build as its writer.</summary>
    private static void AssertDatabaseHeaderIn(string path, FileFormat format)
    {
        Assert.Equal(format.Version, UserVersion(path));
        Assert.Equal(new Dictionary<string, string>
        {
            ["format.owner"] = FileFormats.Owner,
            ["format.kind"] = format.Kind,
            ["writer.name"] = "pathhide",
            ["writer.version"] = "1.0.0",
        }, HeaderRows(path));
    }

    [Fact]
    public void AnEmptyFileHoldsNothing_SoItIsInitializedAsNew()
    {
        var path = PathOf(RecordsStore.FileName);
        File.WriteAllBytes(path, []);

        using (var store = RecordsStore.TryOpen(path, out var failure) ?? throw failure!)
            AssertDatabaseHeaderIn(path, FileFormats.Records);
    }

    [Fact]
    public void FailedInitializationLeavesNoPartialSchema_AndTheNextOpenInitializes()
    {
        var path = PathOf("failed.sqlite3");
        var format = new FileFormat("test", 1);
        Assert.Throws<SqliteException>(() => FileFormats.OpenDatabase(path, format,
            "CREATE TABLE first (x); INVALID SQL;"));
        Assert.Equal(0, UserVersion(path));

        using (FileFormats.OpenDatabase(path, format, "CREATE TABLE IF NOT EXISTS first (x);"))
        {
        }
        AssertDatabaseHeaderIn(path, format);
    }

    // --- records.sqlite3 ---

    [Fact]
    public async Task Records_AtTheirVersion_CompleteMissingSchemaAndRewriteTheWriter()
    {
        var path = PathOf(RecordsStore.FileName);
        CreateDatabase(path, FileFormats.Records.Version, FileFormats.Records.Kind);
        using (var store = RecordsStore.TryOpen(path, out var failure) ?? throw failure!)
            store.Write(new LogEntry("2026-10-05T00:00:00.000Z", "2026-10-05T00:00:00.000Z", "info", "kept", null, null));
        AssertDatabaseHeaderIn(path, FileFormats.Records);
        Assert.Equal("kept", await ReadOnlyMessage(path));
    }

    // A database that holds tables but no header for this format is not this format: it is refused, untouched.
    [Theory]
    [InlineData(0, null, FileFormats.Owner)]
    [InlineData(1, null, FileFormats.Owner)]
    [InlineData(0, "records", FileFormats.Owner)]
    [InlineData(1, "backups", FileFormats.Owner)]
    [InlineData(1, "records", "scriptdock")]
    public void Records_WithoutTheirHeader_AreRefusedAndLeftByteIdentical(int userVersion, string? kind, string owner)
    {
        var path = PathOf(RecordsStore.FileName);
        CreateDatabase(path, userVersion, kind, owner);
        var bytes = File.ReadAllBytes(path);

        var store = RecordsStore.TryOpen(path, out var failure);

        Assert.Null(store);
        Assert.IsType<InvalidDataException>(failure);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    /// <summary>The one record a records database holds, read through the store's owner as the app reads it.</summary>
    private static async Task<string> ReadOnlyMessage(string path)
    {
        using var owner = new SessionLogger(DateTimeOffset.UtcNow, RecordsStore.TryOpen(path, out var failure) ?? throw failure!,
            null, debugEnabled: false, writeInBackground: true);
        var page = await new RecordsReader(owner).ReadPageAsync(new RecordsQuery(null, null, "", null), TestContext.Current.CancellationToken);
        return Assert.Single(page.Records).Message;
    }

    [Fact]
    public async Task Records_RoundTripAtTheirVersion()
    {
        var path = PathOf(RecordsStore.FileName);
        using (var store = RecordsStore.TryOpen(path, out var failure) ?? throw failure!)
            store.Write(new PathHide.Services.LogEntry("2026-10-05T00:00:00.000Z", "2026-10-05T00:00:00.000Z", "info", "kept", null, null));

        AssertDatabaseHeaderIn(path, FileFormats.Records);
        Assert.Equal("kept", await ReadOnlyMessage(path));
    }

    [Fact]
    public void Records_Newer_AreRefusedAndLeftByteIdentical()
    {
        var path = PathOf(RecordsStore.FileName);
        CreateDatabase(path, userVersion: 2, FileFormats.Records.Kind);
        var bytes = File.ReadAllBytes(path);

        var store = RecordsStore.TryOpen(path, out var failure);

        Assert.Null(store);
        Assert.Equal(2, Assert.IsType<NewerFormatException>(failure).Version);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    // --- backups.sqlite3 ---

    private string BackupsPath => PathOf(BackupStore.FileName);

    private long BackupRows()
    {
        using var connection = new SqliteConnection(ConnectionString(BackupsPath, SqliteOpenMode.ReadOnly));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM backups;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Backups_AtTheirVersion_CompleteMissingSchema()
    {
        CreateDatabase(BackupsPath, FileFormats.Backups.Version, FileFormats.Backups.Kind);
        BackupStore.Record(PathOf(AppSettings.FileName), Encoding.UTF8.GetBytes("{}"));
        BackupStore.Close();
        AssertDatabaseHeaderIn(BackupsPath, FileFormats.Backups);
        Assert.Equal(1, BackupRows());
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, null)]
    [InlineData(1, "records")]
    public void Backups_WithoutTheirHeader_AreRefusedAndLeftByteIdentical(int userVersion, string? kind)
    {
        CreateDatabase(BackupsPath, userVersion, kind);
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

        AssertDatabaseHeaderIn(BackupsPath, FileFormats.Backups);
        Assert.Equal(1, BackupRows());
    }

    [Fact]
    public void Backups_Newer_AreRefusedAndLeftByteIdentical()
    {
        CreateDatabase(BackupsPath, userVersion: 2, FileFormats.Backups.Kind);
        var bytes = File.ReadAllBytes(BackupsPath);

        BackupStore.Record(PathOf(AppSettings.FileName), Encoding.UTF8.GetBytes("{}"));
        BackupStore.Close();

        Assert.Equal(bytes, File.ReadAllBytes(BackupsPath));
    }

    // --- launch ---

    // Every refused file is named at once, whatever its kind, and nothing is written before the stop.
    [Fact]
    public void Launch_NamesEveryRefusedFile_AndWritesNothing()
    {
        File.WriteAllText(PathOf(AppSettings.FileName), "{ not valid json");
        WriteNewer(AppState.FileName, """{ "format": { "owner": "pathhide", "kind": "state", "version": 2 } }""");
        WriteNewer(PathListStore.FileName, """{ "format": { "owner": "pathhide", "kind": "path-list", "version": 2 } }""");
        CreateDatabase(BackupsPath, userVersion: 2, FileFormats.Backups.Kind);
        var records = new NewerFormatException(PathOf(RecordsStore.FileName), 2, 1);
        var before = Directory.GetFiles(_root).ToDictionary(path => path, File.ReadAllBytes);

        var refused = Assert.Throws<StartupRefusedException>(() => App.ReadStartupStores(records));

        Assert.Equal(
        [
            new StartupRefusal(PathOf(AppSettings.FileName), RefusalReason.Unreadable),
            new StartupRefusal(PathOf(AppState.FileName), RefusalReason.Newer),
            new StartupRefusal(PathOf(PathListStore.FileName), RefusalReason.Newer),
            new StartupRefusal(PathOf(RecordsStore.FileName), RefusalReason.Newer),
            new StartupRefusal(BackupsPath, RefusalReason.Newer),
        ], refused.Refusals);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root).Order());
        Assert.All(before, file => Assert.Equal(file.Value, File.ReadAllBytes(file.Key)));
    }

    // An unreadable records database is off for the session with a notice; the app starts.
    [Fact]
    public void Launch_WithUnreadableRecords_StartsWithANotice()
    {
        var loaded = App.ReadStartupStores(new InvalidDataException("not this format"));

        Assert.Equal("failure.recordsOff", loaded.RecordsOff?.Key);
        Assert.Null(loaded.BackupsOff);
    }

    // An unreadable backup history is off for the session with a notice, and stays byte-identical
    // through a session of saves.
    [Theory]
    [InlineData(1, null)]
    [InlineData(1, "records")]
    [InlineData(0, "backups")]
    public void Launch_WithUnreadableBackups_StartsWithANotice_AndNeverTouchesThem(int userVersion, string? kind)
    {
        CreateDatabase(BackupsPath, userVersion, kind);
        var bytes = File.ReadAllBytes(BackupsPath);

        var loaded = App.ReadStartupStores();
        new SettingsStore().SaveChanges(null, new AppSettings { Theme = ThemePreference.Dark });
        new PathListStore().Save([Entry("/a")]);
        StateStore().Save(new AppState { WindowWidth = 900 });
        BackupStore.Close();

        Assert.Equal("failure.backupsOff", loaded.BackupsOff?.Key);
        Assert.Equal(bytes, File.ReadAllBytes(BackupsPath));
    }

    [Fact]
    public void Launch_WithAFileThatIsNotADatabaseAsBackups_StartsWithANotice()
    {
        File.WriteAllText(BackupsPath, "not a database at all, just text that is long enough to have a header page");
        var bytes = File.ReadAllBytes(BackupsPath);

        var loaded = App.ReadStartupStores();
        new SettingsStore().SaveChanges(null, new AppSettings { Theme = ThemePreference.Dark });
        BackupStore.Close();

        Assert.Equal("failure.backupsOff", loaded.BackupsOff?.Key);
        Assert.Equal(bytes, File.ReadAllBytes(BackupsPath));
    }

    [AvaloniaFact]
    public void The_side_store_notices_show_under_the_list()
    {
        var vm = App.CreateMainViewModel(new StartupStores(new AppSettings(), new AppState(), [],
            FailurePresentation.RecordsOff(), FailurePresentation.BackupsOff()));

        Assert.Equal(["failure.recordsOff", "failure.backupsOff"], vm.OperationalResults.Select(result => result.Message.Key));
        Assert.All(vm.OperationalResults, result => Assert.True(result.IsError));
    }
}
