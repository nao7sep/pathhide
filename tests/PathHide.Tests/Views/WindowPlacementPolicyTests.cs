using PathHide.Models;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class WindowPlacementPolicyTests
{
    private static readonly DisplayWorkArea[] Displays =
    [
        new(0, 0, 1920, 1080, 1),
        new(-2560, 0, 2560, 2048, 2),
    ];

    [Fact]
    public void Missing_or_malformed_mode_defaults_normal()
    {
        Assert.Equal("normal", WindowPlacementPolicy.Resolve(null, Displays).Mode);
        Assert.Equal("normal", WindowPlacementPolicy.Resolve(
            new WindowPlacement { Mode = "fullscreen" }, Displays).Mode);
    }

    [Fact]
    public void Partial_and_below_minimum_bounds_are_available_for_toolkit_adjustment()
    {
        foreach (var bounds in new[]
        {
            new WindowBounds { X = 1200, Y = 10, Width = 900, Height = 700 },
            new WindowBounds { X = 10, Y = 10, Width = 300, Height = 200 },
            new WindowBounds { X = -2600, Y = 20, Width = 1900, Height = 1300 },
        })
        {
            Assert.Same(bounds, WindowPlacementPolicy.Resolve(
                new WindowPlacement { NormalBounds = bounds }, Displays).NormalBounds);
        }
    }

    [Fact]
    public void Nonpositive_bounds_are_discarded_without_losing_mode()
    {
        var result = WindowPlacementPolicy.Resolve(new WindowPlacement
        {
            Mode = "maximized",
            NormalBounds = new WindowBounds { Width = 0, Height = 700 },
        }, Displays);
        Assert.Null(result.NormalBounds);
        Assert.Equal("maximized", result.Mode);
    }

    [Fact]
    public void Unusable_geometry_falls_back_as_a_unit_but_preserves_mode()
    {
        var result = WindowPlacementPolicy.Resolve(new WindowPlacement
        {
            Mode = "maximized",
            NormalBounds = new WindowBounds { X = 9000, Y = 9000, Width = 1200, Height = 800 },
        }, Displays);
        Assert.Equal("maximized", result.Mode);
        Assert.Null(result.NormalBounds);
    }

}
