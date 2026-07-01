using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
[Collection(StorageRootEnvironment.CollectionName)]
public sealed class JsonStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousHome;

    public JsonStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pathhide-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _previousHome = Environment.GetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, _root);
    }

    public void Dispose()
    {
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
    public void Load_CorruptPrimary_ReturnsDefault_WithNoBakFallback()
    {
        // The .bak last-good sidecar is retired: an unreadable live file falls back to defaults, and its
        // earlier content is recovered, if ever needed, from the data-backup archives — never a .bak.
        var store = new JsonStore<List<PathEntry>>("paths.json", "paths");
        store.Save([new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }]);
        store.Save([
            new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden },
            new PathEntry { Path = "/b", DesiredVisibility = DesiredVisibility.Shown },
        ]);

        File.WriteAllText(PathOf("paths.json"), "{ not valid json");

        var loaded = store.Load();

        Assert.Empty(loaded);
        Assert.False(File.Exists(PathOf("paths.json.bak")));
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
}
