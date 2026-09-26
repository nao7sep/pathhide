using System;
using System.IO;
using PathHide.Backup;
using PathHide.Models;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>
/// The one-time move of the window geometry from <c>config.json</c> to <c>state.json</c>, against
/// real files under a throwaway <c>PATHHIDE_HOME</c> (see <see cref="JsonStoreTests"/>).
/// </summary>
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class WindowStateMigrationTests : IDisposable
{
    private const string LegacyConfig = """
        {
          "language": "ja",
          "uiFontFamily": "Menlo",
          "theme": "dark",
          "windowsHideMode": "hidden_only",
          "windowPositionX": -1200,
          "windowPositionY": 80,
          "windowWidth": 1100.5,
          "windowHeight": 720.25,
          "windowMaximized": true
        }
        """;

    private readonly string _root;
    private readonly string? _previousHome;

    public WindowStateMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pathhide-tests", NanoId.New());
        Directory.CreateDirectory(_root);
        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
        BackupStore.Close();
    }

    public void Dispose()
    {
        BackupStore.Close();
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _previousHome);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private string ConfigPath => Path.Combine(_root, AppSettings.FileName);
    private string StatePath => Path.Combine(_root, AppState.FileName);

    private bool Migrate(out JsonStore<AppState> stateStore)
    {
        var settingsStore = new JsonStore<AppSettings>(AppSettings.FileName, QuarantineJournal.SettingsLabel);
        stateStore = new JsonStore<AppState>(AppState.FileName, QuarantineJournal.StateLabel);
        return WindowStateMigration.Run(ConfigPath, stateStore, settingsStore, settingsStore.Load().Value);
    }

    [Fact]
    public void Run_MovesTheGeometryOnceAndKeepsTheSettings()
    {
        File.WriteAllText(ConfigPath, LegacyConfig);

        Assert.True(Migrate(out var stateStore));

        var state = stateStore.Load().Value;
        Assert.Equal(-1200, state.WindowPositionX);
        Assert.Equal(80, state.WindowPositionY);
        Assert.Equal(1100.5, state.WindowWidth);
        Assert.Equal(720.25, state.WindowHeight);
        Assert.True(state.WindowMaximized);

        var config = File.ReadAllText(ConfigPath);
        Assert.Null(WindowStateMigration.ReadLegacyGeometry(config));
        var settings = new JsonStore<AppSettings>(AppSettings.FileName, QuarantineJournal.SettingsLabel).Load().Value;
        Assert.Equal("ja", settings.Language);
        Assert.Equal("Menlo", settings.UiFontFamily);
        Assert.Equal(ThemePreference.Dark, settings.Theme);

        // Once: the next launch finds nothing left to move.
        Assert.False(Migrate(out _));
    }

    [Fact]
    public void Run_NeverOverwritesAStateFileAlreadyThere()
    {
        // A move interrupted after writing state.json repeats; the state written first stands.
        File.WriteAllText(ConfigPath, LegacyConfig);
        File.WriteAllText(StatePath, """{ "windowPositionX": 5 }""");

        Assert.True(Migrate(out var stateStore));

        Assert.Equal(5, stateStore.Load().Value.WindowPositionX);
        Assert.Null(WindowStateMigration.ReadLegacyGeometry(File.ReadAllText(ConfigPath)));
    }

    [Fact]
    public void Run_WithoutAConfigFileDoesNothing()
    {
        Assert.False(Migrate(out _));
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void ReadLegacyGeometry_CountsNullValuesSoTheyAreClearedToo()
    {
        var state = WindowStateMigration.ReadLegacyGeometry("""{ "windowPositionX": null, "windowMaximized": false }""");

        Assert.NotNull(state);
        Assert.Null(state!.WindowPositionX);
        Assert.False(state.WindowMaximized);
    }

    [Theory]
    [InlineData("""{ "language": "en" }""")]
    [InlineData("[]")]
    public void ReadLegacyGeometry_HasNothingToMoveWithoutTheOldFields(string json)
    {
        Assert.Null(WindowStateMigration.ReadLegacyGeometry(json));
    }

    [Fact]
    public void An_unreadable_state_file_is_set_aside_without_a_notice()
    {
        // Window state is disposable: its reset is logged, never put in front of the user.
        QuarantineJournal.Drain();
        File.WriteAllText(StatePath, "{ not json");

        var loaded = new JsonStore<AppState>(AppState.FileName, QuarantineJournal.StateLabel).Load();

        Assert.True(loaded.WasUnreadable);
        Assert.Null(loaded.Value.WindowPositionX);
        Assert.Empty(QuarantineJournal.Drain());
    }
}
