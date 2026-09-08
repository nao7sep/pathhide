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

    [Fact]
    public void Accepted_restored_bounds_seed_the_normal_landing_rectangle()
    {
        var accepted = new WindowBounds { X = 120, Y = 140, Width = 1340, Height = 880 };
        var opening = new WindowBounds { X = 300, Y = 220, Width = 1200, Height = 800 };

        Assert.Same(accepted, WindowPlacementPolicy.SeedNormalBounds(
            new WindowPlacement { NormalBounds = accepted, Mode = "normal" }, opening));
        Assert.Same(opening, WindowPlacementPolicy.SeedNormalBounds(
            new WindowPlacement { NormalBounds = null, Mode = "maximized" }, opening));
    }

    [Fact]
    public void Native_fullscreen_frame_matches_only_the_complete_display()
    {
        var displays = new[] { new DisplayWorkArea(0, 0, 2560, 1440, 1) };
        Assert.True(WindowPlacementPolicy.IsFullDisplayFrame(
            new WindowBounds { X = 0, Y = 0, Width = 2560, Height = 1440 }, displays));
        Assert.False(WindowPlacementPolicy.IsFullDisplayFrame(
            new WindowBounds { X = 0, Y = 30, Width = 2560, Height = 1311 }, displays));
    }
}
