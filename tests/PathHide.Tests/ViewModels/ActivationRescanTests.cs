using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.ViewModels;

/// <summary>
/// <see cref="MainWindowViewModel.RescanOnActivation"/>: the window coming back to the front rescans
/// the list through the ordinary background scan, except when something else is under way.
/// </summary>
public class ActivationRescanTests
{
    private static PathEntry Entry(string path) =>
        new() { Path = path, DesiredVisibility = DesiredVisibility.Hidden };

    private static FakeJsonStore<List<PathEntry>> List(params string[] paths) =>
        new() { Value = paths.Select(Entry).ToList() };

    [Fact]
    public async Task A_change_made_outside_shows_after_activation_and_the_selection_stays()
    {
        var visibility = new FakeVisibilityService();
        visibility.Set("/a", ActualState.Hidden);
        visibility.Set("/b", ActualState.Hidden);
        var vm = MainWindowViewModelTests.CreateViewModel(visibility, List("/a", "/b"), clock: new ManualClock());
        await vm.ScanTask;
        var rows = vm.Rows.ToList();
        var a = rows.Single(r => r.Path == "/a");
        a.IsSelected = true;

        // Unhidden in Finder or Explorer while the app was in the background.
        visibility.Set("/a", ActualState.Visible);

        Assert.True(vm.RescanOnActivation());
        await vm.ScanTask;

        Assert.Equal(ActualState.Visible, a.ActualState);
        Assert.Equal(ActualState.Hidden, rows.Single(r => r.Path == "/b").ActualState);
        Assert.True(a.IsSelected);
        Assert.Equal(rows, vm.Rows);
    }

    [Fact]
    public async Task No_rescan_starts_while_a_scan_runs()
    {
        using var gate = new ManualResetEventSlim(false);
        var visibility = new FakeVisibilityService { InspectGate = gate };
        var vm = MainWindowViewModelTests.CreateViewModel(visibility, List("/a"), clock: new ManualClock());
        try
        {
            await visibility.InspectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var initialScan = vm.ScanTask;

            Assert.False(vm.RescanOnActivation());
            Assert.Same(initialScan, vm.ScanTask);
        }
        finally
        {
            gate.Set();
        }
        await vm.ScanTask;

        Assert.Single(visibility.Inspected);
    }

    [Fact]
    public async Task No_rescan_starts_while_hide_or_show_runs()
    {
        using var gate = new ManualResetEventSlim(false);
        var visibility = new FakeVisibilityService();
        var vm = MainWindowViewModelTests.CreateViewModel(visibility, List("/a"), clock: new ManualClock());
        await vm.ScanTask;
        vm.Rows.Single().IsSelected = true;
        visibility.WriteGate = gate;
        try
        {
            var show = vm.ShowSelectedCommand.ExecuteAsync(null);
            await visibility.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.False(vm.RescanOnActivation());
            Assert.False(vm.IsScanning);

            gate.Set();
            await show;
        }
        finally
        {
            gate.Set();
        }

        // Once the command is done, the next activation rescans.
        Assert.True(vm.RescanOnActivation());
        await vm.ScanTask;
    }

    [Fact]
    public async Task Activations_within_the_quiet_period_start_one_rescan()
    {
        var clock = new ManualClock();
        var visibility = new FakeVisibilityService();
        var vm = MainWindowViewModelTests.CreateViewModel(visibility, List("/a"), clock: clock);
        await vm.ScanTask;

        Assert.True(vm.RescanOnActivation());
        await vm.ScanTask;
        clock.Advance(MainWindowViewModel.ActivationRescanQuietPeriod - TimeSpan.FromMilliseconds(1));
        Assert.False(vm.RescanOnActivation());

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(vm.RescanOnActivation());
        await vm.ScanTask;

        // The first scan, then one per rescan that started.
        Assert.Equal(3, visibility.Inspected.Count);
    }

    [Fact]
    public void No_rescan_before_the_first_scan()
    {
        var settings = new FakeSettingsStore();
        var vm = new MainWindowViewModel(
            new BoundedVisibility(new FakeVisibilityService()), List("/a"), settings, settings.Load().Value,
            new FakeJsonStore<AppState>(), new AppState());

        Assert.False(vm.RescanOnActivation());
        Assert.False(vm.IsScanning);
    }

    [Fact]
    public void No_rescan_of_an_empty_list()
    {
        var vm = MainWindowViewModelTests.CreateViewModel(new FakeVisibilityService(), List(), clock: new ManualClock());

        Assert.False(vm.RescanOnActivation());
    }

    [Fact]
    public async Task No_rescan_once_closing_has_begun()
    {
        var vm = MainWindowViewModelTests.CreateViewModel(new FakeVisibilityService(), List("/a"), clock: new ManualClock());
        await vm.ScanTask;

        await vm.ShutdownAsync();

        Assert.False(vm.RescanOnActivation());
        Assert.False(vm.IsScanning);
    }
}
