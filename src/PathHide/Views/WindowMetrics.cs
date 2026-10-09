using System.Collections.Generic;
using System.Linq;

namespace PathHide.Views;

/// <summary>
/// Derives the main window's minimum size from the layout itself (window conventions,
/// Content-based minimum size). The path-list DataGrid is the single content pane, so its
/// column minimums drive the window's minimum width; the toolbar, status bar, and a few
/// visible data rows drive the minimum height.
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

    // What "usable or recoverable" means for a restored window (window-conventions, Placement): its title
    // bar can be grabbed and dragged with ordinary window interaction. So the band where the native
    // title bar is drawn on both platforms (about 28 pt on macOS, 31 px at 100 % on Windows) must lie
    // inside one working area from top to bottom (above it, the macOS menu bar or the screen edge takes
    // the clicks), and enough of its width to grab must be on that screen. Values in device-independent
    // units, scaled by the screen.
    private const double TitleBandHeight = 32;
    private const double GrabWidth = 120;

    public static bool CanRestoreWindowGeometry(
        int? x, int? y, double? width, double? height,
        Avalonia.PixelRect workingArea, double scale)
    {
        if (x is not { } savedX || y is not { } savedY
            || width is not > 0 || height is not > 0
            || !double.IsFinite(width.Value) || !double.IsFinite(height.Value)
            || workingArea.Width <= 0 || workingArea.Height <= 0 || scale <= 0)
        {
            return false;
        }

        var band = TitleBandHeight * scale;
        if (savedY < workingArea.Y || savedY + band > (long)workingArea.Y + workingArea.Height)
            return false;

        var left = System.Math.Max((double)savedX, workingArea.X);
        var right = System.Math.Min(savedX + width.Value * scale, (double)workingArea.X + workingArea.Width);
        return right - left >= System.Math.Min(width.Value, GrabWidth) * scale;
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

    // A dialog takes this much of the screen it opens on, never all of it. Not a share of its owner:
    // a dialog is a separate window, and an owner that happens to be small says nothing about how much
    // room the dialog has (modal-dialog conventions).
    public const double DialogHeightFraction = 0.85;

    /// <summary>
    /// The tallest a dialog's content may be: <see cref="DialogHeightFraction"/> of the working area of
    /// the screen it opens on. A working area that cannot be read leaves the dialog unbounded rather
    /// than guessing a height for it.
    /// </summary>
    public static double DialogMaxHeight(double workingAreaHeight, double scale) =>
        workingAreaHeight > 0 && scale > 0 && double.IsFinite(scale)
            ? workingAreaHeight / scale * DialogHeightFraction
            : double.PositiveInfinity;

    // The path-list Border has Margin="12" on all sides and insets the grid a further 6px inside
    // it, so the grid loses 18px of horizontal room on each edge.
    private const double GridHorizontalMargin = (12 + 6) * 2;

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
