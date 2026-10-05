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
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pathhide-tests", NanoId.New());
    private readonly string? _previousDataDir;

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_root);
        _previousDataDir = Environment.GetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, _root);
        BackupStore.Close();
        QuarantineJournal.Drain();
    }

    public void Dispose()
    {
        Log.Shutdown();
        BackupStore.Close();
        QuarantineJournal.Drain();
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

    /// <summary>The sets config.json holds, after checking the format version recorded beside them.</summary>
    private Dictionary<string, JsonElement> StoredSets()
    {
        var stored = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(ConfigPath))!;
        Assert.True(stored.Remove(FormatVersions.JsonKey, out var version));
        Assert.Equal(FormatVersions.Settings, version.GetInt32());
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
    public void Startup_WithNoConfig_WritesNothingAndUsesBuiltIns()
    {
        var vm = App.CreateMainViewModel();

        Assert.Equal(Languages.System, vm.Language);
        Assert.Equal(string.Empty, vm.UiFontFamily);
        Assert.Equal(ThemePreference.System, vm.Theme);
        Assert.False(vm.IsHiddenAndSystem);
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(Path.Combine(_root, AppState.FileName)));
        Assert.False(File.Exists(Path.Combine(_root, "backups.sqlite3")));
    }

    [AvaloniaFact]
    public async Task DialogSave_ChangingThemeStoresExactlyTheme_AndDeletionRestoresBuiltIns()
    {
        using var restoreLanguage = Localizer.Speaking(Localizer.Language);
        var vm = App.CreateMainViewModel();

        Assert.Null(await vm.TryApplySettingsAsync(vm.Language, vm.UiFontFamily, vm.IsHiddenAndSystem, ThemePreference.Dark));

        Assert.Equal("dark", Assert.Single(StoredSets()).Value.GetString());
        Assert.Equal("theme", Assert.Single(StoredSets()).Key);

        File.Delete(ConfigPath);
        var relaunched = App.CreateMainViewModel();
        Assert.Equal(Languages.System, relaunched.Language);
        Assert.Equal(string.Empty, relaunched.UiFontFamily);
        Assert.Equal(ThemePreference.System, relaunched.Theme);
        Assert.False(relaunched.IsHiddenAndSystem);
        Assert.False(File.Exists(ConfigPath));
    }

    [AvaloniaFact]
    public async Task DialogSave_UnchangedDraftLeavesConfigAbsent()
    {
        var vm = App.CreateMainViewModel();

        Assert.Null(await vm.TryApplySettingsAsync(vm.Language, vm.UiFontFamily, vm.IsHiddenAndSystem, vm.Theme));

        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(Path.Combine(_root, "backups.sqlite3")));
    }

    [Theory]
    [InlineData("language", "\"ja\"")]
    [InlineData("uiFontFamily", "\"Menlo\"")]
    [InlineData("theme", "\"dark\"")]
    [InlineData("windowsHideMode", "\"hidden_and_system\"")]
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
        const string original = """{ "theme": "dark" }""";
        File.WriteAllText(ConfigPath, original);

        var loaded = new SettingsStore().Load();

        Assert.False(loaded.WasUnreadable);
        Assert.Equal(ThemePreference.Dark, loaded.Value.Theme);
        Assert.Equal(Languages.System, loaded.Value.Language);
        Assert.Equal(string.Empty, loaded.Value.UiFontFamily);
        Assert.Equal(WindowsHideMode.HiddenOnly, loaded.Value.WindowsHideMode);
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
    public void Load_InvalidSetUsesBuiltIn_LogsItsKey_AndKeepsTheFile(string key, string value)
    {
        var original = "{ \"theme\": \"dark\", \"language\": \"ja\", \"" + key + "\": " + value + " }";
        File.WriteAllText(ConfigPath, original);
        Log.Start(RecordsStore.TryOpen(StorageRoot.RecordsFile, out _), StorageRoot.LogsDirectory);

        var loaded = new SettingsStore().Load();
        Log.Shutdown();

        Assert.False(loaded.WasUnreadable);
        Assert.Equal(key == "theme" ? ThemePreference.System : ThemePreference.Dark, loaded.Value.Theme);
        Assert.Equal(key == "language" ? Languages.System : "ja", loaded.Value.Language);
        Assert.Equal(string.Empty, loaded.Value.UiFontFamily);
        Assert.Equal(WindowsHideMode.HiddenOnly, loaded.Value.WindowsHideMode);
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.GetFiles(_root, "config-*.invalid"));
        Assert.Empty(QuarantineJournal.Drain());
        Assert.Equal(key, JsonSerializer.Deserialize<JsonElement>(Assert.Single(WarningFields())).GetProperty("key").GetString());
    }

    [AvaloniaFact]
    public async Task DialogSave_DropsAnInvalidSet_AndStoresTheFontCleaned()
    {
        using var restoreLanguage = Localizer.Speaking(Localizer.Language);
        const string original = """{ "language": "future-language", "uiFontFamily": "  Inter  " }""";
        File.WriteAllText(ConfigPath, original);
        var vm = App.CreateMainViewModel();
        Assert.Equal(Languages.System, vm.Language);
        Assert.Equal("Inter", vm.UiFontFamily);
        Assert.Equal(Languages.System, LanguageBootstrap.SavedPreference());
        Assert.Equal(original, File.ReadAllText(ConfigPath));

        Assert.Null(await vm.TryApplySettingsAsync(vm.Language, vm.UiFontFamily, vm.IsHiddenAndSystem, ThemePreference.Dark));

        var sets = StoredSets();
        Assert.Equal(new[] { "theme", "uiFontFamily" }, sets.Keys.Order().ToArray());
        Assert.Equal("Inter", sets["uiFontFamily"].GetString());
        Assert.Equal("dark", sets["theme"].GetString());
    }

    [Fact]
    public void SaveChanges_WritesTheFileFromTheDraft_DroppingBuiltInCopiesAndUnknownKeys()
    {
        File.WriteAllText(ConfigPath, """{ "version": 7, "theme": "system", "language": "ja", "windowsHideMode": "future_mode", "old": true }""");
        var store = new SettingsStore();
        var previous = store.Load().Value;

        Assert.True(store.SaveChanges(previous, new AppSettings { Language = "ja", Theme = ThemePreference.Dark }));

        var sets = StoredSets();
        Assert.Equal(new[] { "language", "theme" }, sets.Keys.Order().ToArray());
        Assert.Equal("ja", sets["language"].GetString());
        Assert.Equal("dark", sets["theme"].GetString());
    }

    [Fact]
    public void SaveChanges_SelectingTheBuiltInRemovesTheSet_AndKeepsTheFile()
    {
        File.WriteAllText(ConfigPath, """{ "theme": "dark" }""");
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

    [AvaloniaTheory]
    [InlineData("{ invalid json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Startup_UnreadableConfigQuarantinesWithoutSeedingAReplacement(string original)
    {
        File.WriteAllText(ConfigPath, original);

        var vm = App.CreateMainViewModel();

        Assert.Equal(ThemePreference.System, vm.Theme);
        Assert.False(File.Exists(ConfigPath));
        var quarantine = Assert.Single(Directory.GetFiles(_root, "config-*.invalid"));
        Assert.Equal(original, File.ReadAllText(quarantine));
        Assert.Equal(QuarantineJournal.SettingsLabel, Assert.Single(QuarantineJournal.Drain()).Label);
        AssertBuiltIns(new SettingsStore().Load().Value);
    }

    [AvaloniaFact]
    public async Task DialogSave_ConfigCorruptedAfterOpeningPreservesBytesAndReportsRecovery()
    {
        using var restoreLanguage = Localizer.Speaking(Localizer.Language);
        var vm = App.CreateMainViewModel();
        var notices = new List<(Message Title, Message Body)>();
        vm.ShowNoticeAsync = (title, body) =>
        {
            notices.Add((title, body));
            return Task.CompletedTask;
        };
        const string corrupt = "{ invalid json";
        File.WriteAllText(ConfigPath, corrupt);

        Assert.Null(await vm.TryApplySettingsAsync(vm.Language, vm.UiFontFamily, vm.IsHiddenAndSystem, ThemePreference.Dark));

        Assert.Equal("theme", Assert.Single(StoredSets()).Key);
        Assert.Equal(corrupt, File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "config-*.invalid"))));
        Assert.Equal(Message.Of("quarantine.settingsTitle"), Assert.Single(notices).Title);
        Assert.Equal(Message.Of("quarantine.settingsBody"), Assert.Single(notices).Body);
        Assert.Empty(QuarantineJournal.Drain());
    }

    [AvaloniaFact]
    public void Startup_LegacyGeometryStaysIgnoredWithoutWritingStateOrConfig()
    {
        const string original = """{ "theme": "dark", "windowWidth": 1100, "windowMaximized": true }""";
        File.WriteAllText(ConfigPath, original);

        var vm = App.CreateMainViewModel();

        Assert.Equal(ThemePreference.Dark, vm.Theme);
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        Assert.False(File.Exists(Path.Combine(_root, AppState.FileName)));
    }

    [Fact]
    public void SaveChanges_RecordsTheExactSparseConfigBytes()
    {
        var store = new SettingsStore();
        store.SaveChanges(new AppSettings(), new AppSettings { Theme = ThemePreference.Dark });
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_root, "backups.sqlite3"),
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
