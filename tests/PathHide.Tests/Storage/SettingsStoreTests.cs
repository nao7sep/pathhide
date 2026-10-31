using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using PathHide.Backup;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Storage;

[Collection(StorageRootEnvironment.CollectionName)]
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pathhide-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _previousDataDir;

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_root);
        _previousDataDir = Environment.GetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _root);
        BackupStore.Close();
    }

    public void Dispose()
    {
        Log.Shutdown();
        BackupStore.Close();
        // Closing returns each connection to Microsoft.Data.Sqlite's pool, which keeps its file open;
        // Windows cannot delete an open database file.
        SqliteConnection.ClearAllPools();
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _previousDataDir);
        Directory.Delete(_root, recursive: true);
    }

    private string ConfigPath => Path.Combine(_root, AppSettings.FileName);

    private List<string> WarningFields()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StorageRoot.RecordsFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT fields FROM log_entries WHERE level = 'warn' ORDER BY id";
        using var reader = query.ExecuteReader();
        var fields = new List<string>();
        while (reader.Read())
            fields.Add(reader.GetString(0));
        return fields;
    }

    /// <summary>The sets config.json holds, after checking the identity header written before them.</summary>
    private Dictionary<string, JsonElement> StoredSets()
    {
        var text = File.ReadAllText(ConfigPath);
        using (var document = JsonDocument.Parse(text))
            FileFormats.CheckHeader(document.RootElement, FileFormats.Settings, ConfigPath);
        var stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!;
        Assert.True(stored.Remove(FileFormats.FormatKey));
        Assert.True(stored.Remove(FileFormats.WriterKey));
        return stored;
    }

    private static void AssertBuiltIns(AppSettings settings)
    {
        var builtIn = new AppSettings();
        Assert.Equal(builtIn.Language, settings.Language);
        Assert.Equal(builtIn.UiFontFamily, settings.UiFontFamily);
        Assert.Equal(builtIn.Theme, settings.Theme);
        Assert.Equal(builtIn.WindowsHideMode, settings.WindowsHideMode);
    }

    [AvaloniaFact]
    public async Task Startup_records_the_selected_display_time_zone()
    {
        using var log = new Fakes.StartupDiagnosticLog("config");
        var settings = new AppSettings { DisplayTimeZone = "Europe/Paris" };
        _ = App.CreateMainViewModel(new StartupStores(settings, new AppState(), []));

        var record = await log.Reported.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(settings.DisplayTimeZone, record.Fields!["displayTimeZone"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public void Startup_WithNoConfig_WritesNothingAndUsesBuiltIns()
    {
        var vm = App.CreateMainViewModel(App.ReadStartupStores());

        Assert.Equal(Languages.System, vm.Language);
        Assert.Equal(string.Empty, vm.UiFontFamily);
        Assert.Equal(ThemePreference.System, vm.Theme);
        Assert.False(vm.IsHiddenAndSystem);
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(Path.Combine(_root, AppState.FileName)));
        Assert.False(File.Exists(Path.Combine(_root, BackupStore.FileName)));
    }

    [AvaloniaFact]
    public async Task DialogSave_ChangingThemeStoresExactlyTheme_AndDeletionRestoresBuiltIns()
    {
        using var restoreLanguage = PathHide.Tests.I18n.TestLanguage.Speaking(Localizer.Language);
        var vm = App.CreateMainViewModel(App.ReadStartupStores());

        Assert.Null(await vm.TryApplySettingsAsync(vm.Language, vm.UiFontFamily, vm.IsHiddenAndSystem, ThemePreference.Dark));

        Assert.Equal("dark", Assert.Single(StoredSets()).Value.GetString());
        Assert.Equal("theme", Assert.Single(StoredSets()).Key);

        File.Delete(ConfigPath);
        var relaunched = App.CreateMainViewModel(App.ReadStartupStores());
        Assert.Equal(Languages.System, relaunched.Language);
        Assert.Equal(string.Empty, relaunched.UiFontFamily);
        Assert.Equal(ThemePreference.System, relaunched.Theme);
        Assert.False(relaunched.IsHiddenAndSystem);
        Assert.False(File.Exists(ConfigPath));
    }

    [AvaloniaFact]
    public async Task DialogSave_UnchangedDraftLeavesConfigAbsent()
    {
        var vm = App.CreateMainViewModel(App.ReadStartupStores());

        Assert.Null(await vm.TryApplySettingsAsync(vm.Language, vm.UiFontFamily, vm.IsHiddenAndSystem, vm.Theme));

        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(Path.Combine(_root, BackupStore.FileName)));
    }

    [Theory]
    [InlineData("language", "\"ja\"")]
    [InlineData("uiFontFamily", "\"Menlo\"")]
    [InlineData("theme", "\"dark\"")]
    [InlineData("windowsHideMode", "\"hidden_and_system\"")]
    [InlineData("displayTimeZone", "\"Asia/Tokyo\"")]
    public void SaveChanges_ChangingOneSetStoresOnlyItsKey(string key, string json)
    {
        var store = new SettingsStore();
        var previous = store.Load().Value;
        var current = SettingsSets.Read(new Dictionary<string, JsonElement>
        {
            [key] = JsonSerializer.Deserialize<JsonElement>(json),
        }, out var invalid);
        Assert.Empty(invalid);

        Assert.True(store.SaveChanges(previous, current));

        var saved = Assert.Single(StoredSets());
        Assert.Equal(key, saved.Key);
        Assert.Equal(json, saved.Value.GetRawText());
    }

    [Fact]
    public void Load_OneSetLeavesOtherSetsAtTheirBuiltIns_AndDoesNotRewrite()
    {
        const string original = """{ "format":{"owner":"pathhide","kind":"config","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "theme": "dark" }""";
        File.WriteAllText(ConfigPath, original);

        var loaded = new SettingsStore().Load();

        Assert.False(loaded.WasUnreadable);
        Assert.Equal(ThemePreference.Dark, loaded.Value.Theme);
        Assert.Equal(Languages.System, loaded.Value.Language);
        Assert.Equal(string.Empty, loaded.Value.UiFontFamily);
        Assert.Equal(WindowsHideMode.HiddenOnly, loaded.Value.WindowsHideMode);
        Assert.Equal(DisplayZone.System, loaded.Value.DisplayTimeZone);
        Assert.Equal(original, File.ReadAllText(ConfigPath));
    }

    [Theory]
    [InlineData("theme", "\"future_theme\"")]
    [InlineData("theme", "2")]
    [InlineData("theme", "\"2\"")]
    [InlineData("theme", "\"light, dark\"")]
    [InlineData("windowsHideMode", "\"future_mode\"")]
    [InlineData("windowsHideMode", "1")]
    [InlineData("windowsHideMode", "\"1\"")]
    [InlineData("language", "null")]
    [InlineData("language", "\"future-language\"")]
    [InlineData("language", "[]")]
    [InlineData("uiFontFamily", "{}")]
    [InlineData("uiFontFamily", "false")]
    // A zone is chosen from the platform's IANA list, so a name not on it is not a zone.
    [InlineData("displayTimeZone", "\"Mars/Olympus\"")]
    [InlineData("displayTimeZone", "\"Tokyo Standard Time\"")]
    [InlineData("displayTimeZone", "9")]
    public void Load_InvalidSetUsesBuiltIn_LogsItsKey_AndKeepsTheFile(string key, string value)
    {
        var theme = key == "theme" ? value : "\"dark\"";
        var language = key == "language" ? value : "\"ja\"";
        var other = key is "theme" or "language" ? "" : ", \"" + key + "\": " + value;
        var original = "{ \"format\":{\"owner\":\"pathhide\",\"kind\":\"config\",\"version\":1},\"writer\":{\"name\":\"pathhide\",\"version\":\"1.0.0\"}, \"theme\": " + theme + ", \"language\": " + language + other + " }";
        File.WriteAllText(ConfigPath, original);
        Log.Start(RecordsStore.TryOpen(StorageRoot.RecordsFile, out _), StorageRoot.LogsDirectory);

        var loaded = new SettingsStore().Load();
        Log.Shutdown();

        Assert.False(loaded.WasUnreadable);
        Assert.Equal(key == "theme" ? ThemePreference.System : ThemePreference.Dark, loaded.Value.Theme);
        Assert.Equal(key == "language" ? Languages.System : "ja", loaded.Value.Language);
        Assert.Equal(string.Empty, loaded.Value.UiFontFamily);
        Assert.Equal(WindowsHideMode.HiddenOnly, loaded.Value.WindowsHideMode);
        Assert.Equal(DisplayZone.System, loaded.Value.DisplayTimeZone);
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.GetFiles(_root, "config-*.invalid"));
        Assert.Equal(key, JsonSerializer.Deserialize<JsonElement>(Assert.Single(WarningFields())).GetProperty("key").GetString());
    }

    [AvaloniaFact]
    public async Task DialogSave_KeepsAnInvalidSetTheUserLeftAlone_AndStoresTheFontCleaned()
    {
        using var restoreLanguage = PathHide.Tests.I18n.TestLanguage.Speaking(Localizer.Language);
        const string original = """{ "format":{"owner":"pathhide","kind":"config","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "language": "future-language", "uiFontFamily": "  Inter  " }""";
        File.WriteAllText(ConfigPath, original);
        var vm = App.CreateMainViewModel(App.ReadStartupStores());
        Assert.Equal(Languages.System, vm.Language);
        Assert.Equal("Inter", vm.UiFontFamily);
        Assert.Equal(Languages.System, LanguageBootstrap.SavedPreference());
        Assert.Equal(original, File.ReadAllText(ConfigPath));

        Assert.Null(await vm.TryApplySettingsAsync(vm.Language, vm.UiFontFamily, vm.IsHiddenAndSystem, ThemePreference.Dark));

        var sets = StoredSets();
        Assert.Equal(new[] { "language", "theme", "uiFontFamily" }, sets.Keys.Order().ToArray());
        Assert.Equal("future-language", sets["language"].GetString());
        Assert.Equal("Inter", sets["uiFontFamily"].GetString());
        Assert.Equal("dark", sets["theme"].GetString());

        // Choosing a language replaces the value it could not read.
        Assert.Null(await vm.TryApplySettingsAsync("ja", vm.UiFontFamily, vm.IsHiddenAndSystem, ThemePreference.Dark));
        Assert.Equal("ja", StoredSets()["language"].GetString());
    }

    [Fact]
    public void SaveChanges_WritesTheDraft_DroppingBuiltInCopies_AndKeepsUnknownKeysAndUntouchedInvalidSets()
    {
        File.WriteAllText(ConfigPath, """{ "format":{"owner":"pathhide","kind":"config","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "version": 7, "theme": "system", "language": "ja", "windowsHideMode": "future_mode", "old": true }""");
        var store = new SettingsStore();
        var previous = store.Load().Value;

        Assert.True(store.SaveChanges(previous, new AppSettings { Language = "ja", Theme = ThemePreference.Dark }));

        var sets = StoredSets();
        Assert.Equal(new[] { "language", "old", "theme", "version", "windowsHideMode" }, sets.Keys.Order().ToArray());
        Assert.Equal("ja", sets["language"].GetString());
        Assert.Equal("dark", sets["theme"].GetString());
        Assert.Equal("future_mode", sets["windowsHideMode"].GetString());
        Assert.True(sets["old"].GetBoolean());
        Assert.Equal(7, sets["version"].GetInt32());
    }

    [Fact]
    public void SaveChanges_SelectingTheBuiltInRemovesTheSet_AndKeepsTheFile()
    {
        File.WriteAllText(ConfigPath, """{ "format":{"owner":"pathhide","kind":"config","version":1},"writer":{"name":"pathhide","version":"1.0.0"}, "theme": "dark" }""");
        var store = new SettingsStore();

        Assert.True(store.SaveChanges(store.Load().Value, new AppSettings()));

        Assert.Empty(StoredSets());
    }

    [Fact]
    public void SaveChanges_UnchangedAfterCleanupWritesNothing()
    {
        var store = new SettingsStore();

        Assert.False(store.SaveChanges(new AppSettings(), new AppSettings { UiFontFamily = "  ", Language = " system " }));

        Assert.False(File.Exists(ConfigPath));
    }

    // config.json holds the user's settings: launch stops rather than run on built-ins over it.
    [Theory]
    [InlineData("{ invalid json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Startup_UnreadableConfigStopsTheLaunchAndLeavesTheFile(string original)
    {
        File.WriteAllText(ConfigPath, original);

        var refused = Assert.Throws<StartupRefusedException>(() => App.ReadStartupStores());

        Assert.Equal(new StartupRefusal(ConfigPath, RefusalReason.Unreadable), Assert.Single(refused.Refusals));
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        Assert.Equal(["config.json"], Directory.EnumerateFiles(_root).Select(Path.GetFileName));
    }

    // A settings file that became unreadable during the session is not saved over: Save says so.
    [AvaloniaFact]
    public async Task DialogSave_RefusesAConfigThatBecameUnreadable_AndLeavesIt()
    {
        using var restoreLanguage = PathHide.Tests.I18n.TestLanguage.Speaking(Localizer.Language);
        var vm = App.CreateMainViewModel(App.ReadStartupStores());
        File.WriteAllText(ConfigPath, "{ invalid json");

        var failure = await vm.TryApplySettingsAsync(vm.Language, vm.UiFontFamily, vm.IsHiddenAndSystem, ThemePreference.Dark);

        Assert.Equal("failure.settingsUnreadable", failure?.Key);
        Assert.Equal("{ invalid json", File.ReadAllText(ConfigPath));
        Assert.Equal(ThemePreference.System, vm.Theme);
    }

    [MacOnlyFact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void An_inaccessible_config_stops_the_launch_as_one_that_could_not_be_opened()
    {
        const string original = "{\"format\":{\"owner\":\"pathhide\",\"kind\":\"config\",\"version\":1},\"writer\":{\"name\":\"pathhide\",\"version\":\"1.0.0\"},\"theme\":\"dark\"}";
        File.WriteAllText(ConfigPath, original);
        var mode = File.GetUnixFileMode(ConfigPath);
        File.SetUnixFileMode(ConfigPath, UnixFileMode.None);
        try
        {
            var refused = Assert.Throws<StartupRefusedException>(() => App.ReadStartupStores());
            Assert.Equal(RefusalReason.Inaccessible, Assert.Single(refused.Refusals).Reason);
            Assert.Throws<UnreadableStoreException>(() => new SettingsStore().SaveChanges(null, new AppSettings { Theme = ThemePreference.Light }));
        }
        finally
        {
            File.SetUnixFileMode(ConfigPath, mode);
        }
        Assert.Equal(original, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void SaveChanges_RecordsTheExactSparseConfigBytes()
    {
        var store = new SettingsStore();
        store.SaveChanges(new AppSettings(), new AppSettings { Theme = ThemePreference.Dark });
        BackupStore.Close();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_root, BackupStore.FileName),
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM backups WHERE path = $path";
        command.Parameters.AddWithValue("$path", ConfigPath);

        Assert.Equal(File.ReadAllBytes(ConfigPath), Assert.IsType<byte[]>(command.ExecuteScalar()));
        Assert.Single(StoredSets());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }
}
