using PathHide.Models;
using PathHide.Storage;

namespace PathHide.Tests.Fakes;

public sealed class FakeSettingsStore : FakeJsonStore<AppSettings>, ISettingsStore
{
    public void SaveChanges(AppSettings previous, AppSettings current) => Save(current);
}
