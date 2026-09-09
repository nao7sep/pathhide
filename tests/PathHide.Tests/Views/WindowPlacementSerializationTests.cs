using System.Text.Json;
using PathHide.Models;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class WindowPlacementSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new WindowPlacementsConverter() },
    };

    [Fact]
    public void Null_placement_is_treated_as_missing_state()
    {
        var restored = JsonSerializer.Deserialize<WindowPlacements>("null", Options);
        Assert.NotNull(restored);
        Assert.Null(restored.Main);
    }

    [Fact]
    public void Logical_client_dimensions_survive_the_existing_state_boundary()
    {
        var source = new WindowPlacements
        {
            Main = new WindowPlacement
            {
                Mode = "maximized",
                NormalBounds = new WindowBounds
                {
                    X = -1200, Y = 80, Width = 1600, Height = 1000,
                    ClientWidth = 790.5, ClientHeight = 480.25,
                },
            },
        };
        var restored = JsonSerializer.Deserialize<WindowPlacements>(
            JsonSerializer.Serialize(source, Options), Options)!;
        Assert.Equal("maximized", restored.Main!.Mode);
        Assert.Equal(790.5, restored.Main.NormalBounds!.ClientWidth);
        Assert.Equal(480.25, restored.Main.NormalBounds.ClientHeight);
        Assert.Equal(1600, restored.Main.NormalBounds.Width);
    }

    [Fact]
    public void Legacy_frame_records_remain_readable()
    {
        var restored = JsonSerializer.Deserialize<WindowPlacements>("""
            {"main":{"normalBounds":{"x":20,"y":40,"width":1200,"height":800},"mode":"normal"}}
            """, Options)!;
        Assert.Equal(1200, restored.Main!.NormalBounds!.Width);
        Assert.Null(restored.Main.NormalBounds.ClientWidth);
        Assert.Equal("normal", restored.Main.Mode);
    }

    [Fact]
    public void Malformed_bounds_do_not_erase_maximized_mode()
    {
        var restored = JsonSerializer.Deserialize<WindowPlacements>("""
            {"main":{"normalBounds":{"x":20,"y":40,"width":"bad","height":800},"mode":"maximized"}}
            """, Options)!;
        Assert.Null(restored.Main!.NormalBounds);
        Assert.Equal("maximized", restored.Main.Mode);
    }
}
