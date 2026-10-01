using PathHide.Models;

namespace PathHide.Storage;

/// <summary>Settings are read as effective values and saved only for the sets the user changed.</summary>
public interface ISettingsStore
{
    LoadedStore<AppSettings> Load();
    void SaveChanges(AppSettings previous, AppSettings current);
}
