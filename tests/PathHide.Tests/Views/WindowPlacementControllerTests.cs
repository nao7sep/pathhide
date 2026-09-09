using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PathHide.Models;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class WindowPlacementControllerTests
{
    [Theory]
    [InlineData(WindowState.Normal, false, true, WindowState.Maximized)]
    [InlineData(WindowState.Maximized, false, false, WindowState.Normal)]
    [InlineData(WindowState.Normal, true, false, WindowState.Minimized)]
    [InlineData(WindowState.Maximized, true, true, WindowState.Minimized)]
    [InlineData(WindowState.FullScreen, false, false, WindowState.FullScreen)]
    [InlineData(WindowState.Normal, false, false, WindowState.Normal)]
    public void Native_windows_state_wins_over_lagging_managed_notifications(
        WindowState managed, bool iconic, bool zoomed, WindowState expected)
    {
        Assert.Equal(expected, WindowPlacementController.ResolveWindowsState(managed, iconic, zoomed));
    }

    [AvaloniaTheory]
    [InlineData("normal", WindowState.Normal)]
    [InlineData("maximized", WindowState.Maximized)]
    public void Geometry_failure_keeps_the_saved_mode_and_actual_default_bounds(
        string mode, WindowState expectedState)
    {
        var saved = new List<WindowPlacement>();
        var errors = new List<Exception>();
        var failure = new InvalidOperationException("display preparation failed");
        var window = new Window { Width = 900, Height = 600, Content = new Border() };
        var placement = new WindowPlacementController(window,
            new WindowPlacement { Mode = mode }, saved.Add, errors.Add,
            _ => throw failure);

        // Screen discovery and geometry preparation belong to the contained
        // native lifecycle pass, not construction of the app's main window.
        Assert.Empty(errors);
        window.Show();
        Assert.Equal(expectedState, window.WindowState);
        placement.Flush();
        var last = Assert.Single(saved);
        Assert.Equal(mode, last.Mode);
        Assert.Equal(900, last.NormalBounds!.ClientWidth);
        Assert.Equal(600, last.NormalBounds.ClientHeight);
        Assert.Same(failure, Assert.Single(errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Immediate_close_saves_the_latest_normal_size_and_position()
    {
        var saved = new List<WindowPlacement>();
        var errors = new List<Exception>();
        var window = new Window { Width = 900, Height = 600, Content = new Border() };
        window.Show();
        var placement = new WindowPlacementController(window,
            new WindowPlacement { Mode = "normal" }, saved.Add, errors.Add);
        Dispatcher.UIThread.RunJobs();
        window.Width = 1000;
        window.Height = 700;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.Position = new PixelPoint(120, 80);
        window.Closing += (_, _) => placement.Flush();
        window.Close();

        Assert.NotEmpty(saved);
        var last = saved[^1];
        Assert.Equal("normal", last.Mode);
        Assert.Equal(120, last.NormalBounds!.X);
        Assert.Equal(80, last.NormalBounds.Y);
        Assert.Equal(1000, last.NormalBounds.ClientWidth);
        Assert.Equal(700, last.NormalBounds.ClientHeight);
        Assert.Empty(errors);
    }

    [AvaloniaFact]
    public void Transient_modes_preserve_the_last_stable_mode_and_normal_bounds()
    {
        var saved = new List<WindowPlacement>();
        var errors = new List<Exception>();
        var window = new Window { Width = 900, Height = 600 };
        window.Show();
        var placement = new WindowPlacementController(window,
            new WindowPlacement { Mode = "normal" }, saved.Add, errors.Add);
        Dispatcher.UIThread.RunJobs();
        placement.Flush();
        var normal = saved[^1].NormalBounds;
        window.WindowState = WindowState.Maximized;
        // No timer tick between maximizing and entering a transient state.
        window.WindowState = WindowState.Minimized;
        placement.Flush();
        Assert.Equal("maximized", saved[^1].Mode);
        Assert.Equal(normal!.ClientWidth, saved[^1].NormalBounds!.ClientWidth);
        Assert.Equal(normal.ClientHeight, saved[^1].NormalBounds!.ClientHeight);

        window.WindowState = WindowState.Normal;
        placement.Flush();
        normal = saved[^1].NormalBounds;
        window.WindowState = WindowState.FullScreen;
        placement.Flush();
        Assert.Equal("normal", saved[^1].Mode);
        Assert.Equal(normal!.ClientWidth, saved[^1].NormalBounds!.ClientWidth);
        Assert.Equal(normal.ClientHeight, saved[^1].NormalBounds!.ClientHeight);
        window.Close();
        Assert.Empty(errors);
    }

    [AvaloniaFact]
    public void A_large_normal_window_is_not_classified_as_fullscreen()
    {
        var window = new Window { Width = 1920, Height = 1080 };
        window.Show();
        Assert.Equal(WindowState.Normal, WindowPlacementController.ReadWindowState(window));
        window.Close();
    }

    [AvaloniaFact]
    public void Save_failure_is_reported_without_blocking_close()
    {
        var errors = new List<Exception>();
        var failure = new InvalidOperationException("placement store failure");
        var window = new Window { Width = 900, Height = 600 };
        window.Show();
        var placement = new WindowPlacementController(window,
            new WindowPlacement { Mode = "normal" }, _ => throw failure, errors.Add);
        window.Closing += (_, _) => placement.Flush();
        window.Close();
        Assert.Same(failure, Assert.Single(errors));
        Assert.False(window.IsVisible);
    }
}
