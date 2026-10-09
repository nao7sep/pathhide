using PathHide.Models;

namespace PathHide.Storage;

/// <summary>Settings are read and written by set (config-sets-conventions).</summary>
public interface ISettingsStore
{
    LoadedStore<AppSettings> Load();
    /// <summary>
    /// Writes the file from <paramref name="current"/> when any set differs from <paramref name="previous"/>,
    /// or always when <paramref name="previous"/> is null because what is on disk is not known, and returns
    /// whether it wrote.
    /// </summary>
    bool SaveChanges(AppSettings? previous, AppSettings current);
}
