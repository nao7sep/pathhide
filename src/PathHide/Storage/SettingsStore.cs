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
    /// <c>config.json</c> is not read first. PathHide is the file's only writer while it runs, and its four
    /// sets are harmless preferences, so a file that could not be used at startup is simply replaced by
    /// this save (see <see cref="SettingsSets"/> for what it drops).
    /// </summary>
    public bool SaveChanges(AppSettings? previous, AppSettings current)
    {
        var sets = SettingsSets.Differing(current);
        if (previous is not null && SettingsSets.SameSets(SettingsSets.Differing(previous), sets))
            return false;

        _store.Save(sets);
        return true;
    }
}
