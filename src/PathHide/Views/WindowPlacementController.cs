using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using PathHide.Models;

namespace PathHide.Views;

// Avalonia 12 has no restore-bounds/save-geometry facility. This is the sole
// placement owner; layout remains responsible for the window's live minimums.
public sealed class WindowPlacementController
{
    private readonly Window _window;
    private readonly Action<WindowPlacement> _save;
    private readonly Action<Exception> _report;
    private readonly DispatcherTimer _timer;
    private readonly Action _restore;
    private WindowBounds? _normalBounds;
    private string _mode;
    private bool _ready;
    private bool _closed;

    public WindowPlacementController(
        Window window, WindowPlacement? saved,
        Action<WindowPlacement> save, Action<Exception> report,
        Action<Screen>? prepareDisplay = null)
    {
        _window = window;
        _save = save;
        _report = report;
        // Mode has no dependency on screen enumeration or disposable geometry.
        _mode = WindowPlacementPolicy.Resolve(saved, Array.Empty<DisplayWorkArea>()).Mode;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Flush();
        _restore = () =>
        {
            var displays = window.Screens.All.Select(WorkArea).ToArray();
            var restoration = WindowPlacementPolicy.Resolve(saved, displays);
            var bounds = restoration.NormalBounds;
            var display = bounds is not null ? WindowPlacementPolicy.FindDisplay(bounds, displays)
                : window.Screens.Primary is { } primary ? WorkArea(primary) : null;
            if (display is not null)
            {
                var targetScreen = window.Screens.All.FirstOrDefault(screen => WorkArea(screen) == display);
                if (targetScreen is not null)
                    prepareDisplay?.Invoke(targetScreen);
                var client = window.ClientSize;
                var frame = window.FrameSize ?? client;
                var chrome = new Size(
                    Math.Max(0, frame.Width - client.Width),
                    Math.Max(0, frame.Height - client.Height));
                // Convert old physical frame records once. New records use the
                // matching logical client size and never need frame correction.
                var width = bounds is null ? window.Width
                    : ValidSize(bounds.ClientWidth) ? bounds.ClientWidth!.Value
                    : bounds.Width / display.Scaling - chrome.Width;
                var height = bounds is null ? window.Height
                    : ValidSize(bounds.ClientHeight) ? bounds.ClientHeight!.Value
                    : bounds.Height / display.Scaling - chrome.Height;
                window.Width = Math.Max(window.MinWidth,
                    Math.Min(width, display.Width / display.Scaling - chrome.Width));
                window.Height = Math.Max(window.MinHeight,
                    Math.Min(height, display.Height / display.Scaling - chrome.Height));
                if (bounds is not null)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Position = new PixelPoint(
                        (int)Math.Clamp((long)bounds.X, display.X,
                            (long)display.X + Math.Max(0, display.Width - (window.Width + chrome.Width) * display.Scaling)),
                        (int)Math.Clamp((long)bounds.Y, display.Y,
                            (long)display.Y + Math.Max(0, display.Height - (window.Height + chrome.Height) * display.Scaling)));
                }
            }
        };

        window.PositionChanged += OnPositionChanged;
        window.PropertyChanged += OnPropertyChanged;
        window.Opened += OnOpened;
        window.Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            window.PositionChanged -= OnPositionChanged;
            window.PropertyChanged -= OnPropertyChanged;
            window.Opened -= OnOpened;
        };
        if (window.IsVisible)
            CompleteRestoration();
    }

    private static DisplayWorkArea WorkArea(Screen screen) => new(
        screen.WorkingArea.X, screen.WorkingArea.Y,
        screen.WorkingArea.Width, screen.WorkingArea.Height, screen.Scaling);

    private static bool ValidSize(double? value) =>
        value is > 0 && double.IsFinite(value.Value);

    private void OnOpened(object? sender, EventArgs e) => CompleteRestoration();

    private void CompleteRestoration()
    {
        if (_ready || _closed)
            return;
        try
        {
            // Opened is the public lifecycle point with measured frame geometry.
            // Apply one normal restoration, lay it out, then maximize.
            _restore();
            _window.UpdateLayout();
        }
        catch (Exception ex)
        {
            _report(ex);
        }
        try
        {
            // Keep the actual default landing rectangle if geometry was unusable.
            _normalBounds = Snapshot();
        }
        catch (Exception ex)
        {
            _report(ex);
        }
        try
        {
            // A failed geometry attempt must not erase an independent saved mode.
            if (_mode == "maximized")
                _window.WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            _report(ex);
        }
        _ready = true;
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => Schedule();

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == TopLevel.ClientSizeProperty)
            Schedule();
    }

    private void Schedule()
    {
        if (!_ready || _closed)
            return;
        _timer.Stop();
        // The native state query excludes maximize/fullscreen geometry even when
        // Avalonia's managed notification lags. Cache ordinary bounds now so a
        // maximize immediately after a move does not lose its landing rectangle.
        try
        {
            Capture();
        }
        catch (Exception ex)
        {
            _report(ex);
        }
        _timer.Start();
    }

    private void Capture()
    {
        var state = ReadWindowState(_window);
        if (state == WindowState.Normal)
        {
            _mode = "normal";
            _normalBounds = Snapshot();
        }
        else if (state == WindowState.Maximized)
        {
            _mode = "maximized";
        }
    }

    private WindowBounds Snapshot()
    {
        var frame = _window.FrameSize ?? _window.ClientSize;
        var scale = _window.RenderScaling;
        return new WindowBounds
        {
            X = _window.Position.X,
            Y = _window.Position.Y,
            Width = (int)Math.Round(frame.Width * scale),
            Height = (int)Math.Round(frame.Height * scale),
            ClientWidth = _window.ClientSize.Width,
            ClientHeight = _window.ClientSize.Height,
        };
    }

    public void Flush()
    {
        if (_closed)
            return;
        _timer.Stop();
        CompleteRestoration();
        try
        {
            Capture();
        }
        catch (Exception ex)
        {
            _report(ex);
        }
        try
        {
            // Persist retained placement even if the final geometry query failed.
            _save(new WindowPlacement { NormalBounds = _normalBounds, Mode = _mode });
        }
        catch (Exception ex)
        {
            _report(ex);
        }
    }

    public static WindowState ReadWindowState(Window window)
    {
        // On Windows, native maximize/restore geometry is reported before
        // Avalonia's managed WindowState notification. Read the HWND's state.
        if (OperatingSystem.IsWindows()
            && window.TryGetPlatformHandle() is { HandleDescriptor: "HWND", Handle: not 0 } native)
            return ResolveWindowsState(window.WindowState, IsIconic(native.Handle), IsZoomed(native.Handle));

        // Avalonia's Cocoa getter is explicitly marked unusable in 12.1.2 and its
        // managed state can lag native fullscreen notifications. Ask NSWindow
        // directly rather than guessing from a frame that happens to fill a screen.
        if (OperatingSystem.IsMacOS()
            && window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle { NSWindow: not 0 } handle)
        {
            var mask = SendUnsigned(handle.NSWindow, Selector("styleMask"));
            if ((mask & (1UL << 14)) != 0) // NSWindowStyleMaskFullScreen
                return WindowState.FullScreen;
            if (SendBoolean(handle.NSWindow, Selector("isMiniaturized")) != 0)
                return WindowState.Minimized;
            return SendBoolean(handle.NSWindow, Selector("isZoomed")) != 0
                ? WindowState.Maximized : WindowState.Normal;
        }
        return window.WindowState;
    }

    public static WindowState ResolveWindowsState(WindowState managed, bool iconic, bool zoomed) =>
        managed == WindowState.FullScreen ? WindowState.FullScreen
        : iconic ? WindowState.Minimized
        : zoomed ? WindowState.Maximized : WindowState.Normal;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern ulong SendUnsigned(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte SendBoolean(IntPtr receiver, IntPtr selector);
}
