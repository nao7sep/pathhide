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
        Assert.Equal("normal", WindowPlacementPolicy.Resolve(null, 900, 600, Displays).Mode);
        Assert.Equal("normal", WindowPlacementPolicy.Resolve(
            new WindowPlacement { Mode = "fullscreen" }, 900, 600, Displays).Mode);
    }

    [Fact]
    public void Bounds_must_meet_scaled_minimum_and_fit_one_work_area()
    {
        Assert.True(WindowPlacementPolicy.IsUsable(
            new WindowBounds { X = -2400, Y = 20, Width = 1900, Height = 1300 }, 900, 600, Displays));
        Assert.False(WindowPlacementPolicy.IsUsable(
            new WindowBounds { X = 10, Y = 10, Width = 899, Height = 700 }, 900, 600, Displays));
        Assert.False(WindowPlacementPolicy.IsUsable(
            new WindowBounds { X = 1200, Y = 10, Width = 900, Height = 700 }, 900, 600, Displays));
    }

    [Fact]
    public void Unusable_geometry_falls_back_as_a_unit_but_preserves_mode()
    {
        var result = WindowPlacementPolicy.Resolve(new WindowPlacement
        {
            Mode = "maximized",
            NormalBounds = new WindowBounds { X = 9000, Y = 9000, Width = 1200, Height = 800 },
        }, 900, 600, Displays);
        Assert.Equal("maximized", result.Mode);
        Assert.Null(result.NormalBounds);
    }
}
