using PathHide.Models;
using PathHide.Storage;

namespace PathHide.Tests.Fakes;

public sealed class FakeSettingsStore : FakeJsonStore<AppSettings>, ISettingsStore
{
    public bool SaveChanges(AppSettings? previous, AppSettings current)
    {
        if (previous is not null && SettingsSets.SameSets(SettingsSets.Differing(previous), SettingsSets.Differing(current)))
            return false;
        Save(current);
        return true;
    }
}
