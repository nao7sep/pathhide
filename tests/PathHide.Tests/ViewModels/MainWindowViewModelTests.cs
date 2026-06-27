using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.ViewModels;

/// <summary>
/// Orchestration tests for <see cref="MainWindowViewModel"/> through its public
/// constructor, with in-memory fakes for the visibility service and both stores.
/// Covers add/dedup, save-failure rollback, apply summary strings, the status-bar
/// summary, the settings (Windows hide mode) flow, and the construct/initialize split.
/// </summary>
public class MainWindowViewModelTests
{
    private static PathEntry Entry(string path) =>
        new() { Path = path, DesiredVisibility = DesiredVisibility.Hidden };

    /// <summary>
    /// Builds a view model and runs <see cref="MainWindowViewModel.Initialize"/>, mirroring
    /// what the window does on load. The settings instance is the store's own value, exactly
    /// as the composition root wires it (the visibility service closes over that instance).
    /// </summary>
    private static MainWindowViewModel CreateViewModel(
        FakeVisibilityService visibility,
        FakeJsonStore<List<PathEntry>> paths,
        FakeJsonStore<AppSettings>? settings = null)
    {
        var settingsStore = settings ?? new FakeJsonStore<AppSettings>();
        var vm = new MainWindowViewModel(visibility, paths, settingsStore, settingsStore.Load());
        vm.Initialize();
        return vm;
    }

    [Fact]
    public async Task AddPaths_NormalizesDeduplicatesAndRejectsRelative()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/foo", "/foo", "relative" });

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

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/existing" });
        Assert.Single(vm.Rows);

        paths.ThrowOnSave = true;
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/new" });

        // The failed add is fully rolled back: the list is unchanged and the failure surfaced.
        var row = Assert.Single(vm.Rows);
        Assert.Equal("/existing", row.Path);
        Assert.Contains("Failed to save", vm.Notification);
    }

    [Fact]
    public async Task AddPaths_RefusesToRunConcurrently_WhileOneIsInFlight()
    {
        // The guard that promoting AddPaths to a [RelayCommand] buys: while one add is running,
        // a second cannot start — so two rapid drops (or a drop during a picker add) can't
        // interleave their pause/resume and corrupt the shared scan state.
        var gate = new ManualResetEventSlim(false);
        var visibility = new FakeVisibilityService { InspectGate = gate };
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        // Start an add and hold it inside ApplyDesiredState's off-thread inspection (the gate
        // blocks Inspect), so the command is still in flight when we probe it.
        var inFlight = vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });
        Assert.False(vm.AddPathsCommand.CanExecute(new[] { "/y" }));

        gate.Set();
        await inFlight;

        // Once it finishes, the command is runnable again.
        Assert.True(vm.AddPathsCommand.CanExecute(new[] { "/y" }));
        Assert.Single(vm.Rows);
    }

    [AvaloniaFact]
    public async Task CancellingARunningScan_StopsItMidwayAndClearsTheScanningFlag()
    {
        // The reachable purpose of RunScanAsync's `ReferenceEquals(_scanCts, scanCts)` guards:
        // cancelling an in-flight scan interrupts it (later entries are never inspected) and the
        // finally block resets IsScanning — but only because the cancelled scan is still the
        // current one. Run on the headless UI thread so scan progress marshals as it does live.
        var gate = new ManualResetEventSlim(false);
        var visibility = new FakeVisibilityService { InspectGate = gate };
        visibility.Set("/a", ActualState.Hidden);
        visibility.Set("/b", ActualState.Visible);
        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value = new List<PathEntry> { Entry("/a"), Entry("/b") },
        };

        // Initialize starts the background scan, which blocks inside the first entry's inspection.
        var vm = CreateViewModel(visibility, paths);
        Assert.True(vm.IsScanning);

        vm.CancelScanCommand.Execute(null);
        gate.Set();

        // Let the cancelled scan unwind on the UI thread.
        for (var i = 0; i < 200 && vm.IsScanning; i++)
            await Task.Delay(10);

        Assert.False(vm.IsScanning);
        // Cancellation took effect before the second entry: /a was inspected, /b never was.
        Assert.Contains("/a", visibility.Inspected);
        Assert.DoesNotContain("/b", visibility.Inspected);
    }

    [Fact]
    public async Task ShowSelected_FlipsDesiredVisibilityAndApplies()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });

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
        vm.ConfirmDestructiveAsync = _ => Task.FromResult(true);
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });

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
        vm.ConfirmDestructiveAsync = _ => Task.FromResult(false);
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });

        vm.Rows.Single().IsSelected = true;
        await ((IAsyncRelayCommand)vm.RemoveSelectedCommand).ExecuteAsync(null);

        Assert.Single(vm.Rows);
    }

    [Theory]
    [InlineData(1, "1 selected entry from the list?")]
    [InlineData(3, "3 selected entries from the list?")]
    public async Task RemoveSelected_RaisesDestructiveConfirm_WithSpecificLabelAndCountAwareCopy(
        int count, string expectedMessageTail)
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        ConfirmRequest? captured = null;
        // Decline, so nothing is removed — this test pins the request payload, not the outcome.
        vm.ConfirmDestructiveAsync = request =>
        {
            captured = request;
            return Task.FromResult(false);
        };

        await vm.AddPathsCommand.ExecuteAsync(Enumerable.Range(0, count).Select(i => $"/p{i}").ToArray());
        foreach (var row in vm.Rows)
            row.IsSelected = true;

        await ((IAsyncRelayCommand)vm.RemoveSelectedCommand).ExecuteAsync(null);

        Assert.NotNull(captured);
        // The destructive action must carry a specific, danger-styled label — never a generic
        // "Yes"/"OK" — and count-aware singular/plural copy (the modal-conventions fix).
        Assert.Equal("Remove", captured!.ConfirmLabel);
        Assert.Equal("Remove entries", captured.Title);
        Assert.EndsWith(expectedMessageTail, captured.Message);
        // Declined: every row is still present.
        Assert.Equal(count, vm.Rows.Count);
    }

    [Fact]
    public async Task RemoveSelected_WhenNothingSelected_DoesNotPromptForConfirmation()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        var prompted = false;
        vm.ConfirmDestructiveAsync = _ =>
        {
            prompted = true;
            return Task.FromResult(true);
        };

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });
        // No row selected.
        await ((IAsyncRelayCommand)vm.RemoveSelectedCommand).ExecuteAsync(null);

        // An empty selection short-circuits before the confirm — no spurious dialog.
        Assert.False(prompted);
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

    // --- Construct / Initialize split (no I/O in the constructor) ---

    [Fact]
    public void Constructor_DoesNotLoadEntries_UntilInitialize()
    {
        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value = new List<PathEntry> { Entry("/a"), Entry("/b") },
        };
        var settingsStore = new FakeJsonStore<AppSettings>();
        var vm = new MainWindowViewModel(new FakeVisibilityService(), paths, settingsStore, settingsStore.Load());

        // Construction is side-effect-free: the persisted entries are not read yet.
        Assert.Empty(vm.Rows);

        vm.Initialize();

        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public void Initialize_IsIdempotent_SecondCallDoesNotReload()
    {
        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value = new List<PathEntry> { Entry("/a") },
        };
        var settingsStore = new FakeJsonStore<AppSettings>();
        var vm = new MainWindowViewModel(new FakeVisibilityService(), paths, settingsStore, settingsStore.Load());

        vm.Initialize();
        vm.Initialize();

        Assert.Single(vm.Rows);
        // Single(Rows) alone would pass even without the guard (SyncRowsWithEntries is
        // itself idempotent), so assert the guard's real effect: the second call must not
        // re-load the path list (which would also restart the scan).
        Assert.Equal(1, paths.LoadCount);
    }

    // --- Windows hide mode (settings) ---

    [Fact]
    public void SetWindowsHideMode_PersistsAndUpdatesSharedSettingsInstance()
    {
        var settingsStore = new FakeJsonStore<AppSettings>();
        var settings = settingsStore.Load();
        var vm = new MainWindowViewModel(
            new FakeVisibilityService(), new FakeJsonStore<List<PathEntry>>(), settingsStore, settings);

        Assert.False(vm.IsHiddenAndSystem);

        vm.SetWindowsHideMode(true);

        Assert.True(vm.IsHiddenAndSystem);
        // The very instance the visibility service reads is updated — no separate sync step.
        Assert.Equal(WindowsHideMode.HiddenAndSystem, settings.WindowsHideMode);
        Assert.Equal(1, settingsStore.SaveCount);
    }

    [Fact]
    public void SetWindowsHideMode_RaisesPropertyChangedForIsHiddenAndSystem()
    {
        var settingsStore = new FakeJsonStore<AppSettings>();
        var vm = new MainWindowViewModel(
            new FakeVisibilityService(), new FakeJsonStore<List<PathEntry>>(), settingsStore, settingsStore.Load());

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SetWindowsHideMode(true);

        // IsHiddenAndSystem is a plain getter now; a bound view must still be told it changed.
        Assert.Contains(nameof(MainWindowViewModel.IsHiddenAndSystem), changed);
    }

    [Fact]
    public void SetWindowsHideMode_WhenUnchanged_DoesNotSave()
    {
        var settingsStore = new FakeJsonStore<AppSettings>();
        var settings = settingsStore.Load(); // defaults to HiddenOnly
        var vm = new MainWindowViewModel(
            new FakeVisibilityService(), new FakeJsonStore<List<PathEntry>>(), settingsStore, settings);

        vm.SetWindowsHideMode(false);

        Assert.Equal(0, settingsStore.SaveCount);
    }

    [Fact]
    public void SetWindowsHideMode_WhenSaveFails_RevertsInMemoryAndNotifies()
    {
        var settingsStore = new FakeJsonStore<AppSettings> { ThrowOnSave = true };
        var settings = settingsStore.Load();
        var vm = new MainWindowViewModel(
            new FakeVisibilityService(), new FakeJsonStore<List<PathEntry>>(), settingsStore, settings);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SetWindowsHideMode(true);

        // The failed save leaves memory (and what the service reads) on the old value...
        Assert.False(vm.IsHiddenAndSystem);
        Assert.Equal(WindowsHideMode.HiddenOnly, settings.WindowsHideMode);
        Assert.Contains("Failed to save settings", vm.Notification);
        // ...and must not announce a hide-mode change that was rolled back.
        Assert.DoesNotContain(nameof(MainWindowViewModel.IsHiddenAndSystem), changed);
    }

    // --- Apply error handling ---

    [Fact]
    public async Task ApplyDesiredState_WhenHideThrowsGenericError_CountsAsErrorAndRechecks()
    {
        var visibility = new FakeVisibilityService();
        visibility.OnHide = _ => new IOException("write failed (test)");
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        // A newly added entry defaults to Hidden and is applied immediately, so Hide runs.
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });

        Assert.Contains("1 errors", vm.Notification);
        Assert.Contains("/x", visibility.Inspected); // re-inspected after the failure
    }

    [Fact]
    public async Task ApplyDesiredState_AccessDeniedOffWindows_IsErrorNotElevatedRetry()
    {
        // The Windows branch launches a real elevated process, so only assert the
        // non-Windows routing here; on Windows this scenario is the elevation path.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var visibility = new FakeVisibilityService();
        visibility.OnHide = _ => new UnauthorizedAccessException("denied (test)");
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });

        Assert.Contains("1 errors", vm.Notification);
        Assert.DoesNotContain("elevated", vm.Notification);
    }

    [Fact]
    public async Task ApplyDesiredState_AccessDeniedAtInspectOffWindows_IsErrorNoWriteAttempt()
    {
        // A path that is access-denied at INSPECT time surfaces as AccessDenied. On
        // Windows this routes into the elevated retry bucket (a UAC retry, per the
        // README's access-denied promise), but that branch launches a real elevated
        // process, so only the non-Windows routing is asserted here: off Windows there
        // is no elevation step, so AccessDenied stays a terminal error and the Hide write
        // is never attempted.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var visibility = new FakeVisibilityService();
        visibility.Set("/x", ActualState.AccessDenied);
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });

        Assert.Contains("1 errors", vm.Notification);
        Assert.DoesNotContain("elevated", vm.Notification);
        // The write boundary is never crossed for an access-denied inspect.
        Assert.DoesNotContain("/x", visibility.Hidden);
    }

    // --- Elevated-retry verdict mapping (DecideElevatedRow) ---
    //
    // The elevated child reports each path's outcome; the parent maps that report (plus its
    // own re-inspection) to a per-row applied/error verdict and a displayed state. These pin
    // that mapping, including a path the child reports as failed and the UAC-cancelled
    // (no report) case.

    [Fact]
    public void DecideElevatedRow_ChildConfirmsSuccess_OverAccessDenied_IsAppliedAndShowsDesiredState()
    {
        // The canonical elevation case: the path is under a permission wall, so the unelevated
        // re-inspection still reads AccessDenied even though the elevated child changed it. The
        // old re-inspection-only logic miscounted this as an error.
        var (display, applied) = MainWindowViewModel.DecideElevatedRow(
            DesiredVisibility.Hidden, childOk: true, new PathInspection(ActualState.AccessDenied, ItemKind.Unknown));

        Assert.True(applied);
        Assert.Equal(ActualState.Hidden, display);
    }

    [Fact]
    public void DecideElevatedRow_ChildConfirmsSuccess_WhenReadable_ShowsReinspectedState()
    {
        var (display, applied) = MainWindowViewModel.DecideElevatedRow(
            DesiredVisibility.Hidden, childOk: true, new PathInspection(ActualState.Hidden, ItemKind.File));

        Assert.True(applied);
        Assert.Equal(ActualState.Hidden, display);
    }

    [Fact]
    public void DecideElevatedRow_ChildReportsFailure_IsErrorAndShowsAccessDenied()
    {
        // A path denied even to the elevated child: reported as failed, never a false success.
        var (display, applied) = MainWindowViewModel.DecideElevatedRow(
            DesiredVisibility.Hidden, childOk: false, new PathInspection(ActualState.AccessDenied, ItemKind.Unknown));

        Assert.False(applied);
        Assert.Equal(ActualState.AccessDenied, display);
    }

    [Fact]
    public void DecideElevatedRow_NoReport_Cancelled_FallsBackToReinspect_IsError()
    {
        // UAC cancelled: no per-path report, nothing changed, re-inspection still denied.
        var (display, applied) = MainWindowViewModel.DecideElevatedRow(
            DesiredVisibility.Hidden, childOk: null, new PathInspection(ActualState.AccessDenied, ItemKind.Unknown));

        Assert.False(applied);
        Assert.Equal(ActualState.AccessDenied, display);
    }

    [Fact]
    public void DecideElevatedRow_NoReport_ButReinspectMatchesDesired_IsApplied()
    {
        var (display, applied) = MainWindowViewModel.DecideElevatedRow(
            DesiredVisibility.Hidden, childOk: null, new PathInspection(ActualState.Hidden, ItemKind.File));

        Assert.True(applied);
        Assert.Equal(ActualState.Hidden, display);
    }

    [Fact]
    public void DecideElevatedRow_Show_ChildConfirms_OverAccessDenied_ShowsVisible()
    {
        var (display, applied) = MainWindowViewModel.DecideElevatedRow(
            DesiredVisibility.Shown, childOk: true, new PathInspection(ActualState.AccessDenied, ItemKind.Unknown));

        Assert.True(applied);
        Assert.Equal(ActualState.Visible, display);
    }
}
