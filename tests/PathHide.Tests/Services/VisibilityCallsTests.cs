using System.Threading.Tasks;
using PathHide.Services;
using PathHide.Tests.Fakes;
using Xunit;

namespace PathHide.Tests.Services;

public class VisibilityCallsTests
{
    // Where the system resolves no spelling, there is nothing to ask: no call runs for it.
    [Fact]
    public async Task A_system_that_resolves_nothing_is_not_asked()
    {
        var service = new FakeVisibilityService { ResolvesPaths = false };

        Assert.Null(await new VisibilityCalls(service).ResolvePathAsync("/a", TestContext.Current.CancellationToken));
        Assert.Empty(service.Resolved);
    }
}
