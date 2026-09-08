using System;
using System.Collections.Generic;
using System.Linq;

namespace PathHide.Models;

public sealed class WindowPlacementState
{
    public WindowPlacements WindowPlacements { get; set; } = new();
}

public sealed class WindowPlacements
{
    public WindowPlacement? Main { get; set; }
}

public sealed class WindowPlacement
{
    public WindowBounds? NormalBounds { get; set; }
    public string Mode { get; set; } = "normal";
}

public sealed class WindowBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed record DisplayWorkArea(int X, int Y, int Width, int Height, double Scaling);

public static class WindowPlacementPolicy
{
    public static WindowPlacement Resolve(
        WindowPlacement? saved,
        double minimumWidth,
        double minimumHeight,
        IEnumerable<DisplayWorkArea> displays)
    {
        var bounds = saved?.NormalBounds;
        return new WindowPlacement
        {
            Mode = saved?.Mode == "maximized" ? "maximized" : "normal",
            NormalBounds = bounds is not null && IsUsable(bounds, minimumWidth, minimumHeight, displays)
                ? bounds
                : null,
        };
    }

    public static bool IsUsable(
        WindowBounds bounds,
        double minimumWidth,
        double minimumHeight,
        IEnumerable<DisplayWorkArea> displays) => displays.Any(display =>
            bounds.Width >= Math.Ceiling(minimumWidth * display.Scaling)
            && bounds.Height >= Math.Ceiling(minimumHeight * display.Scaling)
            && bounds.X >= display.X
            && bounds.Y >= display.Y
            && (long)bounds.X + bounds.Width <= (long)display.X + display.Width
            && (long)bounds.Y + bounds.Height <= (long)display.Y + display.Height);
}
