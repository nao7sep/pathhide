using PathHide.Models;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests;

/// <summary>Which visibility writer and elevation step the app composes for each system, decided anywhere.</summary>
public sealed class PlatformServicesTests
{
    [Fact]
    public void Windows_gets_its_attribute_writer_and_the_elevation_step()
    {
        var (visibility, elevated) = App.CreatePlatformServices(windows: true);

        Assert.IsType<WindowsVisibilityService>(visibility);
        Assert.IsType<ElevatedApplicator>(elevated);
    }

    [Fact]
    public void macOS_gets_its_flag_writer_and_no_elevation_step()
    {
        var (visibility, elevated) = App.CreatePlatformServices(windows: false);

        Assert.IsType<MacVisibilityService>(visibility);
        Assert.Null(elevated);
    }
}
