using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PathHide.Models;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.ViewModels;

/// <summary>
/// Orchestration tests for <see cref="MainWindowViewModel"/> through its internal
/// test-seam constructor, with in-memory fakes for the visibility service and both
/// stores. Covers add/dedup, save-failure rollback, the apply summary strings, and
/// the status-bar summary.
/// </summary>
public class MainWindowViewModelTests
{
    private static PathEntry Entry(string path) =>
        new() { Path = path, DesiredVisibility = DesiredVisibility.Hidden };

    private static MainWindowViewModel CreateViewModel(
        FakeVisibilityService visibility,
        FakeJsonStore<List<PathEntry>> paths,
        FakeJsonStore<AppSettings>? settings = null)
        => new(visibility, paths, settings ?? new FakeJsonStore<AppSettings>());

    [Fact]
    public async Task AddPaths_NormalizesDeduplicatesAndRejectsRelative()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        await vm.AddPathsAsync(new[] { "/foo", "/foo", "relative" });

        var row = Assert.Single(vm.Rows);
        Assert.Equal("/foo", row.Path);
        Assert.Contains("1 added, 2 skipped", vm.Notification);
        Assert.Contains("/foo", visibility.Hidden); // newly added entries default to Hidden and are applied
        Assert.Equal(1, paths.SaveCount);
    }

    [Fact]
    public async Task AddPaths_WhenSaveFails_RollsBackEntriesAndRows()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        await vm.AddPathsAsync(new[] { "/existing" });
        Assert.Single(vm.Rows);

        paths.ThrowOnSave = true;
        await vm.AddPathsAsync(new[] { "/new" });

        // The failed add is fully rolled back: the list is unchanged and the failure surfaced.
        var row = Assert.Single(vm.Rows);
        Assert.Equal("/existing", row.Path);
        Assert.Contains("Failed to save", vm.Notification);
    }

    [Fact]
    public async Task ShowSelected_FlipsDesiredVisibilityAndApplies()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);
        await vm.AddPathsAsync(new[] { "/x" });

        var row = Assert.Single(vm.Rows);
        row.IsSelected = true;
        await ((IAsyncRelayCommand)vm.ShowSelectedCommand).ExecuteAsync(null);

        Assert.Equal(DesiredVisibility.Shown, row.DesiredVisibility);
        Assert.Equal(ActualState.Visible, row.ActualState);
        Assert.Contains("/x", visibility.Shown);
        Assert.Equal("1 applied", vm.Notification);
    }

    [Fact]
    public async Task RemoveSelected_WhenConfirmed_RemovesRowAndPersists()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);
        vm.ConfirmAsync = (_, _) => Task.FromResult(true);
        await vm.AddPathsAsync(new[] { "/x" });

        vm.Rows.Single().IsSelected = true;
        await ((IAsyncRelayCommand)vm.RemoveSelectedCommand).ExecuteAsync(null);

        Assert.Empty(vm.Rows);
        Assert.Contains("1 removed", vm.Notification);
    }

    [Fact]
    public async Task RemoveSelected_WhenDeclined_KeepsRow()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);
        vm.ConfirmAsync = (_, _) => Task.FromResult(false);
        await vm.AddPathsAsync(new[] { "/x" });

        vm.Rows.Single().IsSelected = true;
        await ((IAsyncRelayCommand)vm.RemoveSelectedCommand).ExecuteAsync(null);

        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task StatusBarText_SummarizesActualStatesAfterScan()
    {
        var visibility = new FakeVisibilityService();
        visibility.Set("/a", ActualState.Hidden);
        visibility.Set("/b", ActualState.Visible);
        visibility.Set("/c", ActualState.Missing);

        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value = new List<PathEntry> { Entry("/a"), Entry("/b"), Entry("/c") },
        };
        var vm = CreateViewModel(visibility, paths);

        // ReloadAsync re-runs the scan and awaits it, so all rows have a settled state.
        await ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null);

        Assert.Equal("3 entries  ·  1 hidden  ·  1 visible  ·  1 missing", vm.StatusBarText);
    }

    [Fact]
    public void StatusBarText_WhenEmpty_ShowsGettingStartedHint()
    {
        var vm = CreateViewModel(new FakeVisibilityService(), new FakeJsonStore<List<PathEntry>>());

        Assert.Equal("No entries — drop files or folders here to get started", vm.StatusBarText);
    }
}
