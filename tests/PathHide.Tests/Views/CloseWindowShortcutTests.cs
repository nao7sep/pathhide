using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// Cmd/Ctrl+W closes the window it is pressed in, the way its close button would, in every window the
/// app opens (keyboard-shortcut conventions, Closing a window). On the Mac the menu bar's Close Window
/// item takes the chord first and sends AppKit's own close; these press it where the bar is absent.
/// </summary>
public sealed class CloseWindowShortcutTests : WindowTest
{
    internal static void PressCloseWindow(Window window)
    {
        var modifiers = ShortcutCatalog.CommandModifier(window) == KeyModifiers.Meta
            ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPress(Key.W, modifiers, PhysicalKey.W, "w");
        Dispatcher.UIThread.RunJobs();
        // A window the press closed takes no key-up.
        if (window.IsVisible)
            window.KeyRelease(Key.W, modifiers, PhysicalKey.W, "w");
        Dispatcher.UIThread.RunJobs();
    }

    private static MainWindowViewModel Owner()
    {
        var settings = new FakeSettingsStore();
        return new MainWindowViewModel(
            new VisibilityCalls(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), []);
    }

    [AvaloniaFact]
    public void The_main_window_closes_through_its_own_close()
    {
        var main = Show(new MainWindow { DataContext = Owner() });
        var closing = 0;
        main.Closing += (_, e) => closing += e.CloseReason == WindowCloseReason.WindowClosing ? 1 : 0;
        var closed = false;
        main.Closed += (_, _) => closed = true;

        PressCloseWindow(main);

        Assert.Equal(1, closing);
        Assert.True(closed);
    }

    [AvaloniaFact]
    public void Records_closes_and_leaves_the_main_window_open()
    {
        var main = Show(new MainWindow { DataContext = Owner() });
        main.RecordsReader = () => new FakeRecordsReader();
        var records = Track(main.OpenRecords());
        Dispatcher.UIThread.RunJobs();

        PressCloseWindow(records);

        Assert.False(records.IsVisible);
        Assert.True(main.IsVisible);
    }

    [AvaloniaFact]
    public void A_dialog_closes_and_leaves_its_owner_open()
    {
        var main = Show(new MainWindow { DataContext = Owner() });
        var about = new AboutDialog(_ => true);
        _ = about.ShowDialog(main);
        Track(about);
        Dispatcher.UIThread.RunJobs();

        PressCloseWindow(about);

        Assert.False(about.IsVisible);
        Assert.True(main.IsVisible);
    }
}
