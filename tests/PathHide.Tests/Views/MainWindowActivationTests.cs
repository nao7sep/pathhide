using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>The main window coming back to the front rescans its list, but not under a modal dialog.</summary>
public sealed class MainWindowActivationTests : WindowTest
{
    private static (MainWindowViewModel Vm, FakeVisibilityService Visibility, ManualClock Clock) CreateViewModel()
    {
        var clock = new ManualClock();
        var visibility = new FakeVisibilityService();
        var settings = new FakeSettingsStore();
        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value = [new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }],
        };
        var vm = new MainWindowViewModel(
            new BoundedVisibility(visibility), paths, settings, settings.Load().Value,
            new FakeJsonStore<AppState>(), new AppState())
        {
            Clock = clock,
        };
        return (vm, visibility, clock);
    }

    private static async Task SettleAsync(MainWindowViewModel vm)
    {
        Dispatcher.UIThread.RunJobs();
        await vm.ScanTask;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Coming_back_to_the_front_rescans_the_list()
    {
        var (vm, visibility, clock) = CreateViewModel();
        var main = Show(new MainWindow { DataContext = vm });
        await SettleAsync(vm);
        var scanned = visibility.Inspected.Count;
        Assert.False(vm.IsScanning);

        // Another window takes the front, then the main window comes back.
        Show(new Window());
        clock.Advance(MainWindowViewModel.ActivationRescanQuietPeriod);
        main.Activate();
        await SettleAsync(vm);

        Assert.Equal(scanned + 1, visibility.Inspected.Count);
    }

    [AvaloniaFact]
    public async Task The_first_activation_after_the_first_scan_starts_no_rescan()
    {
        // The window's first activation can arrive after the first scan has finished; the window
        // has not come back to the front, so nothing is scanned again.
        var (vm, visibility, _) = CreateViewModel();
        vm.Initialize();
        await vm.ScanTask;
        var scanned = visibility.Inspected.Count;

        Show(new MainWindow { DataContext = vm });
        await SettleAsync(vm);

        Assert.Equal(scanned, visibility.Inspected.Count);
    }

    [AvaloniaFact]
    public async Task Coming_back_to_the_front_under_a_modal_dialog_starts_no_rescan()
    {
        var (vm, visibility, clock) = CreateViewModel();
        var main = Show(new MainWindow { DataContext = vm });
        await SettleAsync(vm);
        var scanned = visibility.Inspected.Count;

        var dialog = Track(new AboutDialog());
        _ = dialog.ShowBoundedAsync(main);
        Dispatcher.UIThread.RunJobs();
        clock.Advance(MainWindowViewModel.ActivationRescanQuietPeriod);
        main.Activate();
        await SettleAsync(vm);

        Assert.Equal(scanned, visibility.Inspected.Count);
    }
}
