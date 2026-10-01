using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
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

    private Dictionary<string, JsonElement> StoredSets() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(ConfigPath))!;

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
        Assert.Equal(SettingsSets.Theme, Assert.Single(StoredSets()).Key);

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

        store.SaveChanges(previous, current);

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
    [InlineData("windowsHideMode", "\"future_mode\"")]
    [InlineData("windowsHideMode", "1")]
    [InlineData("windowsHideMode", "\"1\"")]
    [InlineData("language", "null")]
    [InlineData("language", "[]")]
    [InlineData("uiFontFamily", "{}")]
    [InlineData("uiFontFamily", "false")]
    public void Load_InvalidSetUsesBuiltIn_LogsOnceByKey_AndKeepsTheFile(string key, string value)
    {
        var original = "{ \"theme\": \"dark\", \"language\": \"ja\", \"" + key + "\": " + value + " }";
        File.WriteAllText(ConfigPath, original);
        var logs = Path.Combine(_root, "logs");
        Log.Start(logs);
        var store = new SettingsStore();

        var loaded = store.Load();
        store.Load();
        Log.Flush();

        Assert.False(loaded.WasUnreadable);
        Assert.Equal(key == SettingsSets.Theme ? ThemePreference.System : ThemePreference.Dark, loaded.Value.Theme);
        Assert.Equal(key == SettingsSets.Language ? Languages.System : "ja", loaded.Value.Language);
        Assert.Equal(string.Empty, loaded.Value.UiFontFamily);
        Assert.Equal(WindowsHideMode.HiddenOnly, loaded.Value.WindowsHideMode);
        Assert.Equal(original, File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.GetFiles(_root, "config-*.invalid"));
        Assert.Empty(QuarantineJournal.Drain());
        var warnings = File.ReadLines(Assert.Single(Directory.GetFiles(logs, "*.log")))
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(line => line.GetProperty("level").GetString() == "warn").ToArray();
        Assert.Equal(key, Assert.Single(warnings).GetProperty("key").GetString());
    }

    [AvaloniaFact]
    public async Task DialogSave_PreservesUnknownLanguageAndExplicitFontWithoutRewritingThem()
    {
        using var restoreLanguage = Localizer.Speaking(Localizer.Language);
        const string original = """{ "language": "future-language", "uiFontFamily": "  Inter  " }""";
        File.WriteAllText(ConfigPath, original);
        var vm = App.CreateMainViewModel();
        Assert.Equal(Languages.System, vm.Language);
        Assert.Equal("  Inter  ", vm.UiFontFamily);
        Assert.Equal(Languages.System, LanguageBootstrap.SavedPreference());
        Assert.Equal(original, File.ReadAllText(ConfigPath));

        Assert.Null(await vm.TryApplySettingsAsync(vm.Language, vm.UiFontFamily, vm.IsHiddenAndSystem, ThemePreference.Dark));

        var sets = StoredSets();
        Assert.Equal("future-language", sets[SettingsSets.Language].GetString());
        Assert.Equal("  Inter  ", sets[SettingsSets.UiFontFamily].GetString());
        Assert.Equal("dark", sets[SettingsSets.Theme].GetString());
        Assert.False(sets.ContainsKey(SettingsSets.WindowsHideMode));
    }

    [Fact]
    public void SaveChanges_ReReadsCurrentMap_PreservesUntouchedCopies_AndDropsUnknownKeys()
    {
        File.WriteAllText(ConfigPath, """{ "version": 7, "schemaVersion": 9, "theme": "system", "old": true }""");
        var store = new SettingsStore();
        var previous = store.Load().Value;
        // Another settings writer's changes since the dialog opened must survive its unrelated Save.
        File.WriteAllText(ConfigPath, """{ "version": 7, "schemaVersion": 9, "language": "ja", "uiFontFamily": "Inter", "theme": "system", "old": true }""");

        store.SaveChanges(previous, new AppSettings { Theme = ThemePreference.Dark });

        var sets = StoredSets();
        Assert.Equal(new[] { SettingsSets.Language, SettingsSets.Theme, SettingsSets.UiFontFamily }, sets.Keys.Order().ToArray());
        Assert.Equal("ja", sets[SettingsSets.Language].GetString());
        Assert.Equal("Inter", sets[SettingsSets.UiFontFamily].GetString());
        Assert.Equal("dark", sets[SettingsSets.Theme].GetString());
    }

    [Fact]
    public void SaveChanges_SelectingTheBuiltInWritesTheChangedSetExplicitly()
    {
        File.WriteAllText(ConfigPath, """{ "theme": "dark" }""");
        var store = new SettingsStore();

        store.SaveChanges(store.Load().Value, new AppSettings());

        var saved = Assert.Single(StoredSets());
        Assert.Equal(SettingsSets.Theme, saved.Key);
        Assert.Equal("system", saved.Value.GetString());
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

        Assert.Equal(SettingsSets.Theme, Assert.Single(StoredSets()).Key);
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
