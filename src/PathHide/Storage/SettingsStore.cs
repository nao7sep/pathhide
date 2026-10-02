using System.Collections.Generic;
using System.Text.Json;
using PathHide.Models;
using PathHide.Services;

namespace PathHide.Storage;

/// <summary>Owns config's set-level reads and writes through the managed atomic JSON store.</summary>
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

    public bool SaveChanges(AppSettings previous, AppSettings current)
    {
        var sets = SettingsSets.Differing(current);
        if (SettingsSets.SameSets(SettingsSets.Differing(previous), sets))
            return false;

        // The process holds SingleInstanceLease. This owner also locks the whole save so its
        // load and atomic write cannot overlap another settings operation within the process.
        lock (_gate)
        {
            // Loading first sets aside a file that cannot be read instead of writing over it
            // (store-recovery-conventions); what it held is not carried into the new file.
            _store.Load();
            _store.Save(sets);
        }
        return true;
    }
}
