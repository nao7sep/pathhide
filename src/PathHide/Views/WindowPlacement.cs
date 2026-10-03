using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using PathHide.Services;

namespace PathHide.Views;

/// <summary>
/// The normal rectangle a durable window reopens at (window conventions, Placement), for each window
/// that has one: the main window and the records window. It follows the window's own moves and
/// resizes while the window is normal, restores a saved rectangle before the first frame, and hands
/// back what to save as the window closes. Its owner stores the values.
/// </summary>
internal sealed class WindowPlacement
{
    private readonly Window _window;
    private readonly string _name;
    private (int X, int Y, double Width, double Height)? _normal;

    // Moving and resizing post work for after the native events settle. Once the window has closed,
    // its platform window is gone and asking it for a screen throws, so late work is dropped.
    private bool _closed;

    internal WindowPlacement(Window window, string name)
    {
        _window = window;
        _name = name;
        window.PositionChanged += (_, _) => RememberAfterNativeEvents();
        window.Resized += (_, _) => RememberAfterNativeEvents();
        window.Opened += (_, _) => Remember();
        window.Closed += (_, _) => _closed = true;
    }

    /// <summary>
    /// Applies a saved rectangle, and on Windows its maximized state, before the window is shown. A
    /// rectangle that no screen can show is left unused, so the window opens at its designed size
    /// where the toolkit puts it. <paramref name="prepare"/> runs first with the screen it lands on.
    /// </summary>
    internal void Restore(int? x, int? y, double? width, double? height, bool maximized, Action<Screen>? prepare = null)
    {
        try
        {
            var target = _window.Screens.All.FirstOrDefault(screen =>
                WindowMetrics.CanRestoreWindowGeometry(x, y, width, height, [screen.WorkingArea]));
            if (target is null)
                return;

            prepare?.Invoke(target);
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Position = new PixelPoint(x!.Value, y!.Value);
            _window.Width = width!.Value;
            _window.Height = height!.Value;
            _normal = (x.Value, y.Value, width.Value, height.Value);
            _window.WindowState = WindowMetrics.RestoredWindowState(maximized, OperatingSystem.IsWindows());
        }
        catch (Exception ex)
        {
            // Placement is disposable. Keep the designed defaults if the display backend or a saved
            // value cannot be used.
            Log.Warn("window geometry restore failed", ex, new { window = _name });
        }
    }

    /// <summary>
    /// What to save as the window closes: its normal rectangle, and whether it is maximized on
    /// Windows. Null while it is minimized or fullscreen, or when it never had a normal rectangle.
    /// </summary>
    internal (int X, int Y, double Width, double Height, bool Maximized)? ForClose()
    {
        Remember();
        if (_window.WindowState is WindowState.Normal or WindowState.Maximized && _normal is { } normal)
        {
            return (normal.X, normal.Y, normal.Width, normal.Height,
                OperatingSystem.IsWindows() && _window.WindowState == WindowState.Maximized);
        }

        return null;
    }

    private void Remember()
    {
        if (_closed || _window.WindowState != WindowState.Normal)
            return;

        // Avalonia reports macOS title-bar zoom as Normal. Judge the settled native frame too,
        // otherwise the zoomed rectangle replaces the actual normal rectangle.
        var screen = _window.Screens.ScreenFromWindow(_window);
        if (screen is not null
            && WindowMetrics.IsMaximizedGeometry(
                _window.FrameSize ?? new Size(_window.Width, _window.Height), screen.WorkingArea, screen.Scaling))
        {
            return;
        }

        _normal = (_window.Position.X, _window.Position.Y, _window.Width, _window.Height);
    }

    private void RememberAfterNativeEvents() => Dispatcher.UIThread.Post(Remember);
}

/// <summary>Brings a window back to the front: out of the Dock or taskbar, shown, and active.</summary>
internal static class WindowActivation
{
    internal static void BringBack(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        if (!window.IsVisible)
            window.Show();
        window.Activate();
    }
}
