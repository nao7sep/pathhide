using System.Collections.Generic;
using System.Text.Json;
using PathHide.Models;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>Owns config's set-level reads and writes through the managed atomic JSON store.</summary>
public sealed class SettingsStore : ISettingsStore
{
    private readonly JsonStore<Dictionary<string, JsonElement>> _store =
        new(AppSettings.FileName, "settings", FormatVersions.Settings);

    public LoadedStore<AppSettings> Load()
    {
        var loaded = _store.Load();
        var settings = SettingsSets.Read(loaded.Value, out var invalidKeys);
        foreach (var key in invalidKeys)
            Log.Warn("config: invalid set, using built-in", new { key });
        return new LoadedStore<AppSettings>(settings, loaded.WasUnreadable);
    }

    /// <summary>
    /// Writes <paramref name="current"/> when its sets differ from <paramref name="previous"/>, the
    /// settings the app holds in memory, or always when <paramref name="previous"/> is null;
    /// Admission still protects newer files; invalid disposable preferences can be replaced.
    /// </summary>
    public bool SaveChanges(AppSettings? previous, AppSettings current)
    {
        var sets = SettingsSets.Differing(current);
        if (previous is not null && SettingsSets.SameSets(SettingsSets.Differing(previous), sets))
            return false;

        _store.Save(sets, compareExistingContent: false);
        return true;
    }
}
