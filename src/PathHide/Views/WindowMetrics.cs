using System.Collections.Generic;
using System.Linq;

namespace PathHide.Views;

/// <summary>
/// Derives the main window's minimum size from the layout itself, per the window-chrome
/// conventions: the minimum is the sum of the content panes' real minimums plus the fixed
/// chrome — never a hand-typed magic constant. The path-list DataGrid is the single content
/// pane, so its column minimums drive the window's minimum width; the toolbar, status bar,
/// and a few visible data rows drive the minimum height.
/// </summary>
/// <remarks>
/// Kept as a pure function over the column minimums (read from the live grid by the caller)
/// so the window minimum and the columns can never drift apart, and so the derivation can be
/// tested without a UI thread.
/// </remarks>
public static class WindowMetrics
{
    // Native frame rounding and decoration measurements can differ from the working area slightly.
    private const double MaximizedSizeTolerance = 8;

    public static Avalonia.Controls.WindowState RestoredWindowState(bool maximized, bool isWindows) =>
        maximized && isWindows
            ? Avalonia.Controls.WindowState.Maximized
            : Avalonia.Controls.WindowState.Normal;

    public static bool CanRestoreWindowGeometry(
        int? x, int? y, double? width, double? height,
        IEnumerable<Avalonia.PixelRect> workingAreas)
    {
        if (x is not { } savedX || y is not { } savedY
            || width is not > 0 || height is not > 0
            || !double.IsFinite(width.Value) || !double.IsFinite(height.Value))
        {
            return false;
        }

        return workingAreas.Any(area =>
            area.Width > 0 && area.Height > 0
            && savedX >= area.X && savedX < (long)area.X + area.Width
            && savedY >= area.Y && savedY < (long)area.Y + area.Height);
    }

    public static bool IsMaximizedGeometry(
        Avalonia.Size frameSize, Avalonia.PixelRect workingArea, double scale)
    {
        if (scale <= 0 || !double.IsFinite(scale))
            return false;

        var workWidth = workingArea.Width / scale;
        var workHeight = workingArea.Height / scale;
        return frameSize.Width >= workWidth - MaximizedSizeTolerance
            && frameSize.Height >= workHeight - MaximizedSizeTolerance;
    }

    public static Avalonia.Size CapMinimumToWorkArea(
        Avalonia.Size contentFloor, Avalonia.PixelRect workArea, double scale, Avalonia.Size chrome) =>
        new(System.Math.Min(contentFloor.Width, System.Math.Max(1, workArea.Width / scale - chrome.Width)),
            System.Math.Min(contentFloor.Height, System.Math.Max(1, workArea.Height / scale - chrome.Height)));

    // A dialog takes this much of what bounds it, never all of it: the web half of the modal-dialog
    // conventions caps at a fraction of the viewport, and a dialog filling its owner's content height
    // exactly reads as bursting out of the window rather than sitting inside it.
    public const double DialogHeightFraction = 0.85;

    /// <summary>
    /// The tallest a dialog's content may be: <see cref="DialogHeightFraction"/> of the content height
    /// of the window that owns it, and no more than the same fraction of the screen's working height.
    /// Pass 0 for <paramref name="ownerContentHeight"/> when the dialog has no owner — the
    /// startup-failure shell — and the screen alone bounds it. A working area that cannot be read
    /// leaves the dialog unbounded rather than guessing a height for it.
    /// </summary>
    public static double DialogMaxHeight(double ownerContentHeight, double workingAreaHeight, double scale)
    {
        var screenBound = workingAreaHeight > 0 && scale > 0 && double.IsFinite(scale)
            ? workingAreaHeight / scale * DialogHeightFraction
            : double.PositiveInfinity;
        var ownerBound = ownerContentHeight > 0
            ? ownerContentHeight * DialogHeightFraction
            : double.PositiveInfinity;

        return System.Math.Min(ownerBound, screenBound);
    }

    // The path-list Border has Margin="12" on all sides, so the grid loses 12px of horizontal
    // room on each edge.
    private const double GridHorizontalMargin = 12 + 12;

    // A real content minimum, tall enough to show a few data rows plus the column header — a
    // declared pane minimum, not an arbitrary number. The chrome heights are NOT declared here:
    // they are measured from the live controls and passed in, because both depend on the
    // user-configurable UI font, and this file already measures the toolbar for the width.
    private const double ContentMinHeight = 180;

    /// <summary>
    /// The minimum window width: the sum of the column minimums plus the list margins and the
    /// vertical scrollbar gutter.
    /// </summary>
    public static double MinWidthFor(
        IEnumerable<double> columnMinWidths,
        double verticalScrollBarGutter)
        => columnMinWidths.Sum() + GridHorizontalMargin + verticalScrollBarGutter;

    /// <summary>
    /// The minimum window height: the measured chrome (toolbar + status bar) plus a content
    /// minimum that keeps several data rows visible.
    /// </summary>
    public static double MinHeightFor(double toolbarHeight, double statusBarHeight)
        => toolbarHeight + statusBarHeight + ContentMinHeight;
}
