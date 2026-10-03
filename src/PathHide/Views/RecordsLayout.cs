using System;

namespace PathHide.Views;

/// <summary>
/// The records window's pane sizes (window conventions, Content-based minimum size): a list pane the
/// user sizes with the splitter, beside the detail pane, which takes the rest. The values mirror the
/// margins and columns in RecordsWindow.axaml.
/// </summary>
internal static class RecordsLayout
{
    /// <summary>The window's margin around both panes.</summary>
    public const double Padding = 16;

    /// <summary>The splitter's column between the panes.</summary>
    public const double Gap = 16;

    /// <summary>
    /// The list pane's bounds and its first width. The minimum still fits the two filter selects side
    /// by side and a row's time and level on one line.
    /// </summary>
    public const double ListMin = 320;
    public const double ListDefault = 380;
    public const double ListMax = 640;

    /// <summary>The detail pane's real minimum: a record's fields and its JSON still read at this width.</summary>
    public const double DetailMin = 420;

    /// <summary>The list's own minimum below its filters: a few rows.</summary>
    public const double ListMinHeight = 160;

    // A pane's own border, above and below it.
    private const double PaneBorders = 2;

    /// <summary>The designed size of a records window opened for the first time.</summary>
    public const double DefaultWidth = 1180;
    public const double DefaultHeight = 720;

    /// <summary>Derived: both pane minimums, the splitter and the margins.</summary>
    public const double MinWidth = Padding * 2 + ListMin + Gap + DetailMin;

    /// <summary>Derived: the measured filter band and the list's minimum, in the list pane's border, with the margins.</summary>
    public static double MinHeightFor(double filtersHeight) => Padding * 2 + PaneBorders + filtersHeight + ListMinHeight;

    /// <summary>A dragged width as the intent it saves: inside the list's bounds, in whole pixels.</summary>
    public static double Intent(double draggedWidth) => Math.Clamp(Math.Round(draggedWidth), ListMin, ListMax);

    /// <summary>
    /// The list's width as shown: the intent, narrowed so the detail pane keeps its minimum in the room
    /// the window has now (<paramref name="available"/>, the width inside the margins), never below the
    /// list's own minimum. The intent itself is unchanged, so the width returns when room does.
    /// </summary>
    public static double DisplayedListWidth(double intent, double available)
    {
        var ceiling = Math.Max(ListMin, Math.Min(ListMax, available - Gap - DetailMin));
        return Math.Clamp(Math.Round(intent), ListMin, ceiling);
    }
}
