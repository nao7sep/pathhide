using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.ViewModels;

/// <summary>
/// Orchestration tests for <see cref="MainWindowViewModel"/> through its public
/// constructor, with in-memory fakes for the visibility service and both stores.
/// Covers add/dedup, the commit-after-save contract, apply summary strings, the status-bar
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
        var vm = new MainWindowViewModel(visibility, paths, settingsStore, settingsStore.Load().Value);
        vm.Initialize();
        return vm;
    }

    [Fact]
    public async Task AddPaths_NormalizesDeduplicatesAndRejectsRelative()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);
        Assert.True(vm.IsPathListEmpty);

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/foo", "/foo", "relative" });

        var row = Assert.Single(vm.Rows);
        Assert.False(vm.IsPathListEmpty);
        Assert.Equal("/foo", row.Path);
        var result = Assert.IsType<PathAddResultViewModel>(vm.PathAddResult);
        Assert.Equal("Added 1 path to the list; 1 hidden; 1 path is already in the list; 1 path was unavailable or invalid.", result.Message);
        Assert.Equal(PathAddResultSeverity.Warning, result.Severity);
        Assert.Equal(result.Message, result.AccessibleName);
        Assert.Equal(AutomationLiveSetting.Polite, result.LiveSetting);
        Assert.Contains("/foo", visibility.Hidden); // newly added entries default to Hidden and are applied
        Assert.Equal(1, paths.SaveCount);
    }

    [Fact]
    public void Path_picker_failure_uses_the_add_receiver_without_diagnostics()
    {
        var vm = CreateViewModel(
            new FakeVisibilityService(),
            new FakeJsonStore<List<PathEntry>>());
        var hostile = new IOException("EACCES IPC /private/tmp/PATHHIDE-PICKER-SENTINEL");

        vm.ReportPathPickerFailure(hostile);

        var result = Assert.IsType<PathAddResultViewModel>(vm.PathAddResult);
        Assert.Equal(PathAddResultSeverity.Error, result.Severity);
        Assert.Contains("path picker", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("EACCES", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PATHHIDE-PICKER-SENTINEL", result.Message, StringComparison.Ordinal);

        vm.ResolvePathPickerFailure();
        Assert.Null(vm.PathAddResult);
    }

    [Fact]
    public async Task AddPaths_DuplicateIsPersistentInformationUntilDismissed()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/same" });
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/same" });

        var result = Assert.IsType<PathAddResultViewModel>(vm.PathAddResult);
        Assert.Equal("1 path is already in the list.", result.Message);
        Assert.Equal(PathAddResultSeverity.Information, result.Severity);
        Assert.Equal(result.Message, result.AccessibleName);
        Assert.Equal(AutomationLiveSetting.Polite, result.LiveSetting);

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/other" });
        Assert.Equal("1 path is already in the list.", vm.PathAddResult?.Message);

        vm.DismissPathAddResultCommand.Execute(null);
        Assert.False(vm.HasPathAddResult);
    }

    [Fact]
    public async Task AddPaths_MixedBatchKeepsVisibilityFailureAndAdmissionResultsTogether()
    {
        var visibility = new FakeVisibilityService
        {
            OnHide = path => path == "/cannot-hide" ? new IOException("denied") : null,
        };
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/existing" });
        Assert.False(vm.HasPathAddResult);

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/cannot-hide", "/existing", "relative" });

        var result = Assert.IsType<PathAddResultViewModel>(vm.PathAddResult);
        Assert.Equal(
            "Added 1 path to the list; 1 path is already in the list; 1 path was unavailable or invalid; 1 path could not be hidden.",
            result.Message);
        Assert.Equal(PathAddResultSeverity.Error, result.Severity);
        Assert.Equal(result.Message, result.AccessibleName);
        Assert.Equal(AutomationLiveSetting.Assertive, result.LiveSetting);
        Assert.DoesNotContain("Error: ", result.AccessibleName, StringComparison.Ordinal);
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal(ActualState.Visible, vm.Rows.Single(row => row.Path == "/cannot-hide").ActualState);
    }

    [Fact]
    public async Task AddPaths_WhenSaveFails_LeavesTheListExactlyAsItWas()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/existing" });
        Assert.Single(vm.Rows);

        paths.ThrowOnSave = true;
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/new" });

        // Nothing is committed until the save lands, so the failed add leaves no trace:
        // the list is what it was and the failure is surfaced.
        var row = Assert.Single(vm.Rows);
        Assert.Equal("/existing", row.Path);
        var result = Assert.IsType<PathAddResultViewModel>(vm.PathAddResult);
        Assert.Contains("path list could not be saved", result.Message);
        Assert.Equal(PathAddResultSeverity.Error, result.Severity);
    }

    [Fact]
    public async Task ShowSelected_WhenSaveFails_LeavesTheRowAloneAndTouchesNoFile()
    {
        // The visibility commands used to stamp the new value onto the live entry and the row
        // first and undo it in the catch. They now build the new list as a value, so a failed
        // save cannot leave the row showing a desired state that is not on disk - and, since
        // the apply runs only after the commit, cannot change a file either.
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });

        var row = Assert.Single(vm.Rows);
        row.IsSelected = true;
        paths.ThrowOnSave = true;
        await ((IAsyncRelayCommand)vm.ShowSelectedCommand).ExecuteAsync(null);

        Assert.Equal(DesiredVisibility.Hidden, row.DesiredVisibility);
        Assert.Equal(DesiredVisibility.Hidden, row.Entry.DesiredVisibility);
        Assert.DoesNotContain("/x", visibility.Shown);
        var failure = Assert.Single(vm.OperationalResults);
        Assert.Contains("path list could not be saved", failure.Message);
        Assert.True(failure.IsError);
        Assert.Equal(AutomationLiveSetting.Assertive, failure.LiveSetting);
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

    [Fact]
    public async Task MutatingCommands_DoNotInterleaveWithEachOther()
    {
        // Each generated AsyncRelayCommand refuses a second run of ITSELF, but nothing stopped
        // Remove from landing in the middle of Add's apply - and both pause the background scan,
        // change the list, and start a scan again afterwards. One gate serializes them, which is
        // what makes "the scan I started is the scan that is running" true by construction rather
        // than something RunScanAsync re-checks wherever it touches shared state.
        using var gate = new ManualResetEventSlim(false);
        var visibility = new FakeVisibilityService { InspectGate = gate };
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        // Park the add inside its apply - the list is already saved and the row is on screen.
        var add = vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });
        await visibility.InspectEntered.Task;
        Assert.Equal(1, paths.SaveCount);
        vm.Rows[0].IsSelected = true;

        // Remove has nothing to await before it saves, so without the gate its whole body runs
        // right here, inside the add.
        var remove = ((IAsyncRelayCommand)vm.RemoveSelectedCommand).ExecuteAsync(null);
        Assert.Single(vm.Rows);
        Assert.Equal(1, paths.SaveCount);

        gate.Set();
        await add;
        await remove;

        // It is not refused, only made to wait its turn.
        Assert.Empty(vm.Rows);
        Assert.Equal(2, paths.SaveCount);
    }

    [AvaloniaFact]
    public async Task CancellingARunningScan_StopsItMidwayAndClearsTheScanningFlag()
    {
        // Cancelling an in-flight scan interrupts it (later entries are never inspected) and the
        // finally block resets IsScanning unconditionally — there is only ever one scan to reset.
        // Run on the headless UI thread so the scan's continuations marshal as they do live.
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

        // Wait until /a's inspection is genuinely in-flight and parked on the gate before cancelling.
        // This is the load-bearing synchronization: without it, cancel can land before the scanner's
        // Task.Run is picked up, which skips the delegate entirely and /a is never inspected.
        await visibility.InspectEntered.Task;

        vm.CancelScanCommand.Execute(null);
        gate.Set();

        // Await the scan to unwind rather than polling IsScanning on a wall-clock budget.
        await vm.ScanTask;

        Assert.False(vm.IsScanning);
        // Cancellation took effect before the second entry: /a was inspected, /b never was.
        Assert.Contains("/a", visibility.Inspected);
        Assert.DoesNotContain("/b", visibility.Inspected);
        // Progress is counted from the results as they arrive, so the number the status bar
        // shows and the rows it describes can never disagree: /a's inspection was entered but
        // abandoned when the cancel landed, so it yielded no result, moved no row, and counts
        // for nothing. Deterministic here only because the count is now made in the consuming
        // loop rather than posted to the dispatcher from a separate progress channel.
        Assert.Equal(0, vm.ScanProgress);
        Assert.Equal(2, vm.ScanTotal);
        Assert.All(vm.Rows, r => Assert.Equal(ActualState.Unknown, r.ActualState));
    }

    [AvaloniaFact]
    public async Task PathListReceiver_RoutesNativeFilesAndNeighboringToolbarDenies()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var settingsStore = new FakeJsonStore<AppSettings>();
        var vm = new MainWindowViewModel(
            visibility,
            paths,
            settingsStore,
            settingsStore.Load().Value);
        var window = new MainWindow { DataContext = vm };
        var source = Path.GetTempFileName();

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var receiver = Assert.IsType<Border>(window.FindControl<Border>("PathListReceiver"));
            var toolbar = Assert.IsType<Border>(window.FindControl<Border>("Toolbar"));
            var storageFile = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(source));
            Assert.NotNull(storageFile);
            using var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateFile(storageFile));
            Assert.True(DragDrop.GetAllowDrop(receiver));
            Assert.True(((IDataTransfer)transfer).Contains(DataFormat.File));

            var receiverPoint = receiver.TranslatePoint(new Point(10, 10), window);
            var toolbarPoint = toolbar.TranslatePoint(new Point(10, 10), window);
            Assert.NotNull(receiverPoint);
            Assert.NotNull(toolbarPoint);

            window.DragDrop(receiverPoint.Value, RawDragEventType.DragEnter, transfer,
                DragDropEffects.Copy, RawInputModifiers.None);
            window.DragDrop(receiverPoint.Value, RawDragEventType.DragOver, transfer,
                DragDropEffects.Copy, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("dropActive", receiver.Classes);

            window.DragDrop(receiverPoint.Value, RawDragEventType.DragLeave, transfer,
                DragDropEffects.Copy, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("dropActive", receiver.Classes);

            window.DragDrop(receiverPoint.Value, RawDragEventType.DragOver, transfer,
                DragDropEffects.Copy, RawInputModifiers.None);
            window.DragDrop(receiverPoint.Value, RawDragEventType.Drop, transfer,
                DragDropEffects.Copy, RawInputModifiers.None);
            await visibility.InspectEntered.Task;
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain("dropActive", receiver.Classes);
            Assert.Equal(Path.GetFullPath(source), Assert.Single(vm.Rows).Path);
            Assert.Equal(1, paths.SaveCount);

            window.DragDrop(toolbarPoint.Value, RawDragEventType.Drop, transfer,
                DragDropEffects.Copy, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(vm.Rows);
            Assert.Equal(1, paths.SaveCount);
        }
        finally
        {
            window.Close();
            File.Delete(source);
        }
    }

    [AvaloniaFact]
    public async Task PathAddResult_ExposesSeverityTextAndLiveSemanticsAtTheReceiver()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var settingsStore = new FakeJsonStore<AppSettings>();
        var vm = new MainWindowViewModel(
            visibility,
            paths,
            settingsStore,
            settingsStore.Load().Value);
        var window = new MainWindow { DataContext = vm };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            await vm.AddPathsCommand.ExecuteAsync(new[] { "relative" });
            Dispatcher.UIThread.RunJobs();

            var surface = Assert.IsType<Border>(window.FindControl<Border>("PathAddResultSurface"));
            Assert.True(surface.IsVisible);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(surface));
            Assert.Equal("1 path was unavailable or invalid.", AutomationProperties.GetName(surface));
            Assert.DoesNotContain(surface.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == "Warning");
        }
        finally
        {
            window.Close();
        }
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
        Assert.Contains("1 visible", vm.StatusBarText);
        Assert.Empty(vm.OperationalResults);
    }

    [Fact]
    public void LoadPersistedState_WhenThePathListIsUnreadable_HaltsRatherThanStartingEmpty()
    {
        // The path list is the user's work product, re-derivable from nothing
        // else on disk. Opening with an empty list would look exactly like
        // losing it, and the first add would then write a fresh file holding
        // only that entry — the user working on top of an apparent loss.
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>> { LoadIsUnreadable = true };
        var settingsStore = new FakeJsonStore<AppSettings>();
        var vm = new MainWindowViewModel(visibility, paths, settingsStore, settingsStore.Load().Value);

        Assert.Throws<PathHide.Storage.PathListUnreadableException>(() => vm.LoadPersistedState());
    }

    [Fact]
    public async Task Reload_WhenThePathListIsUnreadable_KeepsTheRowsOnScreen()
    {
        // Mid-session there is nothing to halt: the rows already shown are the
        // last good state, so they stay rather than being replaced by an empty
        // list, and the user is told what happened.
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/keep-me" });
        Assert.Single(vm.Rows);

        var told = false;
        vm.ShowNoticeAsync = (_, _) => { told = true; return Task.CompletedTask; };
        paths.LoadIsUnreadable = true;
        PathHide.Storage.QuarantineJournal.Record("paths", "/r/paths-x.invalid");

        await ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null);

        Assert.Single(vm.Rows);
        Assert.Equal("/keep-me", vm.Rows[0].Path);
        Assert.True(told);
    }

    [Fact]
    public async Task Reload_WhenTheStoreWasQuarantined_TellsTheUser()
    {
        // The startup drain runs once, in the window's Opened handler. A load
        // that quarantines afterwards - pressing Reload on a paths.json edited
        // into invalid JSON - emptied every row with no notice, no explanation
        // and no safe recovery guidance for the copy that was set aside.
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        (string Title, string Body)? shown = null;
        vm.ShowNoticeAsync = (title, body) =>
        {
            shown = (title, body);
            return Task.CompletedTask;
        };

        // The store finds the file unreadable on this load and sets it aside.
        PathHide.Storage.QuarantineJournal.Record("paths", "/home/u/.pathhide/paths-20260821-000000-000-utc.invalid");

        await ((IAsyncRelayCommand)vm.ReloadCommand).ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Contains("paths", shown!.Value.Title);
        Assert.Contains("session log", shown!.Value.Body);
        Assert.DoesNotContain("/home/u/.pathhide", shown.Value.Body, StringComparison.Ordinal);
        // Drained, so a second reload does not repeat it.
        Assert.Empty(PathHide.Storage.QuarantineJournal.Drain());
    }

    [Fact]
    public void QuarantineNotice_NamesTheStoreThatWasReset()
    {
        // One hardcoded wording told a user whose settings file was reset that
        // their hidden-path list was in a file that does not contain it.
        var settings = PathHide.Storage.QuarantineJournal.Describe(
            [new PathHide.Storage.QuarantinedStore("settings", "/r/config-x.invalid")]);
        Assert.Contains("settings", settings.Title);
        Assert.DoesNotContain("hidden-path list", settings.Body);

        var pathList = PathHide.Storage.QuarantineJournal.Describe(
            [new PathHide.Storage.QuarantinedStore("paths", "/r/paths-x.invalid")]);
        Assert.Contains("paths", pathList.Title);
        Assert.Contains("session log", pathList.Body);
        Assert.DoesNotContain("/r/paths-x.invalid", pathList.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HideAll_And_ShowAll_ActOnEveryRowNotJustTheSelection()
    {
        // The four visibility commands share one body now; these pin that the
        // All variants still take every row while the Selected ones do not.
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/a", "/b" });

        await ((IAsyncRelayCommand)vm.ShowAllCommand).ExecuteAsync(null);
        Assert.All(vm.Rows, r => Assert.Equal(DesiredVisibility.Shown, r.DesiredVisibility));
        Assert.Contains("/a", visibility.Shown);
        Assert.Contains("/b", visibility.Shown);
        Assert.Contains("2 visible", vm.StatusBarText);

        await ((IAsyncRelayCommand)vm.HideAllCommand).ExecuteAsync(null);
        Assert.All(vm.Rows, r => Assert.Equal(DesiredVisibility.Hidden, r.DesiredVisibility));
        Assert.Contains("2 hidden", vm.StatusBarText);
        Assert.Empty(vm.OperationalResults);
    }

    [Fact]
    public async Task HideSelected_WithNothingSelected_LeavesStandingStatusUnchanged()
    {
        var visibility = new FakeVisibilityService();
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);
        await vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });
        var writesBefore = paths.SaveCount;
        var statusBefore = vm.StatusBarText;

        await ((IAsyncRelayCommand)vm.HideSelectedCommand).ExecuteAsync(null);

        Assert.Equal(statusBefore, vm.StatusBarText);
        Assert.Empty(vm.OperationalResults);
        Assert.Equal(writesBefore, paths.SaveCount);
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
        Assert.True(vm.IsPathListEmpty);
        Assert.Equal("No entries — drop files or folders here to get started", vm.StatusBarText);
        Assert.Empty(vm.OperationalResults);
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
        var vm = new MainWindowViewModel(new FakeVisibilityService(), paths, settingsStore, settingsStore.Load().Value);

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
        var vm = new MainWindowViewModel(new FakeVisibilityService(), paths, settingsStore, settingsStore.Load().Value);

        vm.Initialize();
        vm.Initialize();

        Assert.Single(vm.Rows);
        // Single(Rows) alone would pass even without the guard (SyncRowsWithEntries is
        // itself idempotent), so assert the guard's real effect: the second call must not
        // re-load the path list (which would also restart the scan).
        Assert.Equal(1, paths.LoadCount);
    }

    // --- Settings ---

    [Fact]
    public void TryApplySettings_SavesBothFieldsAsOneCandidateBeforePublishingThem()
    {
        var settingsStore = new FakeJsonStore<AppSettings>();
        var settings = settingsStore.Load().Value;
        var vm = new MainWindowViewModel(
            new FakeVisibilityService(), new FakeJsonStore<List<PathEntry>>(), settingsStore, settings);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var failure = vm.TryApplySettings("  Menlo  ", hiddenAndSystem: true);

        Assert.Null(failure);
        Assert.Equal(1, settingsStore.SaveCount);
        Assert.Equal("Menlo", settingsStore.LastSaved!.UiFontFamily);
        Assert.Equal(WindowsHideMode.HiddenAndSystem, settingsStore.LastSaved.WindowsHideMode);
        Assert.Equal("Menlo", settings.UiFontFamily);
        Assert.True(vm.IsHiddenAndSystem);
        Assert.Contains(nameof(MainWindowViewModel.UiFontFamily), changed);
        Assert.Contains(nameof(MainWindowViewModel.IsHiddenAndSystem), changed);
        Assert.Empty(vm.OperationalResults);
    }

    [Fact]
    public void TryApplySettings_WhenUnchanged_DoesNotSave()
    {
        var settingsStore = new FakeJsonStore<AppSettings>();
        var settings = settingsStore.Load().Value;
        var vm = new MainWindowViewModel(
            new FakeVisibilityService(), new FakeJsonStore<List<PathEntry>>(), settingsStore, settings);

        var failure = vm.TryApplySettings(AppSettings.DefaultUiFontFamily, hiddenAndSystem: false);

        Assert.Null(failure);
        Assert.Equal(0, settingsStore.SaveCount);
    }

    [Fact]
    public void TryApplySettings_FailureLeavesBothLiveFieldsUntouchedForDialogRetry()
    {
        var settingsStore = new FakeJsonStore<AppSettings> { ThrowOnSave = true };
        var settings = settingsStore.Load().Value;
        var vm = new MainWindowViewModel(
            new FakeVisibilityService(), new FakeJsonStore<List<PathEntry>>(), settingsStore, settings);

        var failure = vm.TryApplySettings("Menlo", hiddenAndSystem: true);

        Assert.Contains("Settings could not be saved", failure);
        Assert.Equal(AppSettings.DefaultUiFontFamily, settings.UiFontFamily);
        Assert.Equal(WindowsHideMode.HiddenOnly, settings.WindowsHideMode);
        Assert.Empty(vm.OperationalResults);
    }

    [Fact]
    public async Task IndependentOperationalFailuresStackUntilTheirOwnerRecoversOrTheyAreDismissed()
    {
        var visibility = new FakeVisibilityService();
        var settings = new FakeJsonStore<AppSettings>();
        visibility.OnInspect = _ => new IOException("scan failed");
        var scanPaths = new FakeJsonStore<List<PathEntry>>
        {
            Value = new List<PathEntry> { Entry("/x") },
        };
        var vm = CreateViewModel(visibility, scanPaths, settings);
        await vm.ScanTask;
        visibility.OnInspect = null;

        scanPaths.ThrowOnSave = true;
        vm.Rows[0].IsSelected = true;
        await ((IAsyncRelayCommand)vm.ShowSelectedCommand).ExecuteAsync(null);

        Assert.Equal(2, vm.OperationalResults.Count);
        Assert.Contains(vm.OperationalResults, result => result.Owner == OperationalResultOwner.Scan);
        Assert.Contains(vm.OperationalResults, result => result.Owner == OperationalResultOwner.PathStore);

        var pathFailure = vm.OperationalResults.Single(result => result.Owner == OperationalResultOwner.PathStore);
        vm.DismissOperationalResultCommand.Execute(pathFailure);

        var remaining = Assert.Single(vm.OperationalResults);
        Assert.Equal(OperationalResultOwner.Scan, remaining.Owner);
    }

    [Fact]
    public async Task Log_reveal_failure_has_an_independent_authored_owner_until_retry_succeeds()
    {
        var vm = CreateViewModel(new FakeVisibilityService(), new FakeJsonStore<List<PathEntry>>());
        await vm.ScanTask;

        vm.ReportLogRevealFailure();

        var result = Assert.Single(vm.OperationalResults);
        Assert.Equal(OperationalResultOwner.LogReveal, result.Owner);
        Assert.Contains("log could not be shown", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EACCES", result.Message, StringComparison.Ordinal);

        vm.ResolveLogRevealFailure();
        Assert.Empty(vm.OperationalResults);
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

        var result = Assert.IsType<PathAddResultViewModel>(vm.PathAddResult);
        Assert.Contains("1 path could not be hidden", result.Message);
        Assert.Equal(PathAddResultSeverity.Error, result.Severity);
        Assert.Contains("/x", visibility.Inspected); // re-inspected after the failure
    }

    [Fact]
    public async Task ReapplyAll_ClearsTheExactAddFailureAfterItIsCorrected()
    {
        var visibility = new FakeVisibilityService
        {
            OnHide = _ => new IOException("temporarily unavailable"),
        };
        var paths = new FakeJsonStore<List<PathEntry>>();
        var vm = CreateViewModel(visibility, paths);

        await vm.AddPathsCommand.ExecuteAsync(new[] { "/x" });
        Assert.True(vm.HasPathAddResult);

        visibility.OnHide = null;
        await vm.ReapplyAllCommand.ExecuteAsync(null);

        Assert.False(vm.HasPathAddResult);
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

        var result = Assert.IsType<PathAddResultViewModel>(vm.PathAddResult);
        Assert.Contains("1 path could not be hidden", result.Message);
        Assert.DoesNotContain("elevated", result.Message);
        Assert.Equal(PathAddResultSeverity.Error, result.Severity);
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

        var result = Assert.IsType<PathAddResultViewModel>(vm.PathAddResult);
        Assert.Contains("1 path could not be hidden", result.Message);
        Assert.DoesNotContain("elevated", result.Message);
        Assert.Equal(PathAddResultSeverity.Error, result.Severity);
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
