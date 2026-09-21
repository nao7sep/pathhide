using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Threading;

namespace PathHide.Tests;

/// <summary>
/// The base class for a test that puts a window on screen. Show every window through
/// <see cref="Show{T}"/>, which closes it when the test ends, whether the test passed or threw.
///
/// A window left open outlives its test. Headless Avalonia runs the whole assembly in one
/// application, so that window stays attached to the visual tree: the next test's language change
/// retranslates its controls and re-lays out their text, on layouts and glyph runs built under
/// conditions that test knows nothing about. The result is a failure reported against whichever test
/// happened to be running — which is why the suite's count and its failures used to move between
/// runs at one commit, while every failing test passed alone.
/// </summary>
public abstract class WindowTest : IDisposable
{
    private readonly List<Window> _shown = [];

    /// <summary>Shows <paramref name="window"/>, settles the dispatcher, and returns it.</summary>
    protected T Show<T>(T window)
        where T : Window
    {
        Track(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>
    /// Closes <paramref name="window"/> when the test ends without showing it here — for a window the
    /// app itself opens, such as a dialog shown modally over its owner.
    /// </summary>
    protected T Track<T>(T window)
        where T : Window
    {
        _shown.Add(window);
        return window;
    }

    /// <summary>Closes what the test showed, newest first, so an owner outlives what it owns.</summary>
    public void Dispose()
    {
        for (var index = _shown.Count - 1; index >= 0; index--)
            _shown[index].Close();
        _shown.Clear();
        Dispatcher.UIThread.RunJobs();
        GC.SuppressFinalize(this);
    }
}
