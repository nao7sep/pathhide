using System.Collections.Generic;
using System.Text.Json;
using PathHide.Models;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>Owns config's set-level reads and writes through the managed atomic JSON store.</summary>
/// <remarks>
/// <c>config.json</c> holds the user's chosen settings, so a file present but unreadable or newer stops
/// the launch and is never written over (store-recovery-conventions).
/// </remarks>
public sealed class SettingsStore : ISettingsStore
{
    private readonly JsonStore<Dictionary<string, JsonElement>> _store =
        new(AppSettings.FileName, "settings", FileFormats.Settings, haltWhenUnreadable: true);

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
    /// settings the app holds in memory, or always when <paramref name="previous"/> is null. Keys this
    /// build does not know, and invalid values of sets the user left alone, are written back unchanged.
    /// </summary>
    public bool SaveChanges(AppSettings? previous, AppSettings current)
    {
        var sets = SettingsSets.Differing(current);
        if (previous is not null && SettingsSets.SameSets(SettingsSets.Differing(previous), sets))
            return false;

        // What this build does not understand is kept as written: the file is read again here, at the
        // write, so a file that has become unreadable or newer since launch throws before anything is
        // written over it.
        foreach (var (key, value) in SettingsSets.Kept(_store.Load().Value, sets))
            sets[key] = value;
        _store.Save(sets, compareExistingContent: false);
        return true;
    }
}
