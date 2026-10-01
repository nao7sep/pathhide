using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PathHide.Models;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>Owns config's set-level reads and read-modify-write through the managed atomic JSON store.</summary>
public sealed class SettingsStore : ISettingsStore
{
    private readonly JsonStore<Dictionary<string, JsonElement>> _store =
        new(AppSettings.FileName, QuarantineJournal.SettingsLabel);
    private readonly HashSet<string> _warnedKeys = [];
    private readonly object _gate = new();

    public LoadedStore<AppSettings> Load()
    {
        lock (_gate)
        {
            var loaded = _store.Load();
            var settings = SettingsSets.Read(loaded.Value, out var invalidKeys);
            foreach (var key in invalidKeys)
            {
                if (_warnedKeys.Add(key))
                    Log.Warn("config: invalid set, using built-in", new { key });
            }
            return new LoadedStore<AppSettings>(settings, loaded.WasUnreadable);
        }
    }

    public void SaveChanges(AppSettings previous, AppSettings current)
    {
        var changes = SettingsSets.Changes(previous, current);
        if (changes.Count == 0)
            return;

        // The process holds SingleInstanceLease. This owner also locks the whole patch so its
        // re-read and atomic write cannot overlap another settings operation within the process.
        lock (_gate)
        {
            var sets = _store.Load().Value;
            foreach (var key in sets.Keys.Where(key => !SettingsSets.Keys.Contains(key)).ToArray())
                sets.Remove(key);
            foreach (var (key, value) in changes)
                sets[key] = value;
            _store.Save(sets);
        }
    }
}
