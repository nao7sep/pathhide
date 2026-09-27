using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using PathHide.Tests.I18n;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.ViewModels;

/// <summary>
/// The elevated retry for access-denied paths, end to end on every platform: the view model and the
/// real <see cref="ElevatedApplicator"/>, with <see cref="FakeElevatedChild"/> standing in for the
/// UAC-elevated process and a throwaway directory standing in for %TEMP%.
/// </summary>
public sealed class ElevatedRetryTests : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    private readonly string _temp = Directory.CreateTempSubdirectory("pathhide-tests-").FullName;
    private readonly FakeVisibilityService _visibility = new()
    {
        // Every unelevated write is denied, so every path goes to the elevated retry.
        OnHide = _ => new UnauthorizedAccessException("denied (test)"),
    };
    private readonly FakeElevatedChild _child = new();
    private readonly FakeJsonStore<List<PathEntry>> _paths = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ElevatedApplicator Applicator(TimeSpan? childTimeout = null) =>
        new(_child.LaunchAsync, _temp, "/storage-root", childTimeout);

    private MainWindowViewModel ViewModel(
        ElevatedApplicator applicator, TimeSpan? shutdownBound = null, params string[] paths)
    {
        _paths.Value = paths.Select(path => new PathEntry { Path = path, DesiredVisibility = DesiredVisibility.Hidden }).ToList();
        var settingsStore = new FakeJsonStore<AppSettings>();
        var vm = new MainWindowViewModel(
            new BoundedVisibility(_visibility), _paths, settingsStore, settingsStore.Load().Value,
            new FakeJsonStore<AppState>(), new AppState())
        {
            ElevatedApplicator = applicator,
            ShutdownBound = shutdownBound ?? Generous,
        };
        vm.Initialize();
        return vm;
    }

    private static Task HideAll(MainWindowViewModel vm) => ((IAsyncRelayCommand)vm.HideAllCommand).ExecuteAsync(null);

    private static ActualState StateOf(MainWindowViewModel vm, string path) =>
        vm.Rows.Single(row => row.Path == path).ActualState;

    private static string ResultText(MainWindowViewModel vm) => English.Of(Assert.Single(vm.OperationalResults).Message);

    private string[] TempFiles() => Directory.GetFiles(_temp);

    /// <summary>The child hides a path the way the real one does: on disk first, then in its report.</summary>
    private void Hide(FakeElevatedChild.Run run, string path)
    {
        _visibility.Set(path, ActualState.Hidden);
        run.Report(path);
    }

    [Fact(Timeout = 10_000)]
    public async Task AccessDeniedPaths_GoThroughOneChild_AndTakeItsVerdict()
    {
        _child.OnStart = run =>
        {
            foreach (var path in run.Request.ToHide)
                Hide(run, path);
            run.Exit(0);
        };
        var vm = ViewModel(Applicator(), null, "/a", "/b");
        await vm.ScanTask;

        await HideAll(vm);

        Assert.Equal(1, _child.LaunchCount);
        Assert.Equal(["/a", "/b"], _child.Runs[0].Request.ToHide);
        Assert.Equal(ActualState.Hidden, StateOf(vm, "/a"));
        Assert.Equal(ActualState.Hidden, StateOf(vm, "/b"));
        Assert.Empty(vm.OperationalResults);
        Assert.Empty(TempFiles());
    }

    [Fact(Timeout = 10_000)]
    public async Task ADeclinedPrompt_CountsTheRowsAsErrors_AndLeavesNoFiles()
    {
        _child.Decline = true;
        var vm = ViewModel(Applicator(), null, "/a", "/b");
        await vm.ScanTask;

        await HideAll(vm);

        Assert.Equal("2 errors", ResultText(vm));
        Assert.Equal(ActualState.Visible, StateOf(vm, "/a"));
        Assert.Empty(TempFiles());
    }

    [Fact(Timeout = 10_000)]
    public async Task Cancel_DuringTheElevatedWait_LeavesUnreportedRowsUnknown_AndKeepsReportedVerdicts()
    {
        var applicator = Applicator();
        var vm = ViewModel(applicator, null, "/a", "/b");
        await vm.ScanTask;

        var launch = _child.NextLaunch;
        var hide = HideAll(vm);
        var run = await launch;
        Hide(run, "/a");
        vm.CancelCommand.Execute(null);
        await hide;

        Assert.False(vm.IsApplying);
        Assert.Equal(ActualState.Hidden, StateOf(vm, "/a"));
        Assert.Equal(ActualState.Unknown, StateOf(vm, "/b"));
        Assert.Equal("1 applied, 1 cancelled", ResultText(vm));
        Assert.True(applicator.HasRunningChild);
        Assert.True(vm.HasWorkToFinish);

        run.Exit();
    }

    [Fact(Timeout = 10_000)]
    public async Task Cancel_WhileTheConsentPromptIsOpen_EndsTheWaitAtOnce()
    {
        _child.Prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applicator = Applicator();
        var vm = ViewModel(applicator, null, "/a");
        await vm.ScanTask;

        var hide = HideAll(vm);
        await _child.Prompting.Task;
        vm.CancelCommand.Execute(null);
        await hide;

        Assert.Equal(ActualState.Unknown, StateOf(vm, "/a"));
        Assert.Equal("1 cancelled", ResultText(vm));
        // The prompt is still up and could yet start a child, so a new retry must wait for it.
        Assert.True(applicator.HasRunningChild);

        _child.Decline = true;
        _child.Prompt.SetResult();
        await applicator.ReleaseAsync(Generous);
        Assert.False(applicator.HasRunningChild);
        Assert.Empty(TempFiles());
    }

    [Fact(Timeout = 10_000)]
    public async Task AfterCancel_ASecondRetryIsRefusedWhileTheFirstChildRuns()
    {
        var applicator = Applicator();
        var vm = ViewModel(applicator, null, "/a", "/b");
        await vm.ScanTask;

        var launch = _child.NextLaunch;
        var first = HideAll(vm);
        var run = await launch;
        vm.CancelCommand.Execute(null);
        await first;

        await HideAll(vm);

        Assert.Equal(1, _child.LaunchCount);
        Assert.Equal("2 not changed: an earlier administrator change is still running", ResultText(vm));
        Assert.Equal(ActualState.Visible, StateOf(vm, "/a"));
        Assert.Equal(ActualState.Visible, StateOf(vm, "/b"));

        // Once the first child has exited, the retry runs again.
        run.Exit();
        await applicator.ReleaseAsync(Generous);
        _child.OnStart = next =>
        {
            foreach (var path in next.Request.ToHide)
                Hide(next, path);
            next.Exit(0);
        };
        await HideAll(vm);

        Assert.Equal(2, _child.LaunchCount);
        Assert.Equal(ActualState.Hidden, StateOf(vm, "/a"));
        Assert.Empty(vm.OperationalResults);
    }

    [Fact(Timeout = 10_000)]
    public async Task TheLateCompletionOfACancelledChild_ChangesNothing_AndRemovesItsFiles()
    {
        var applicator = Applicator();
        var vm = ViewModel(applicator, null, "/a", "/b");
        await vm.ScanTask;

        var launch = _child.NextLaunch;
        var hide = HideAll(vm);
        var run = await launch;
        vm.CancelCommand.Execute(null);
        await hide;

        var states = vm.Rows.Select(row => row.ActualState).ToList();
        var result = ResultText(vm);
        var saves = _paths.SaveCount;
        Assert.NotEmpty(TempFiles());

        Hide(run, "/a");
        Hide(run, "/b");
        run.Exit(0);
        await applicator.ReleaseAsync(Generous);

        Assert.Equal(states, vm.Rows.Select(row => row.ActualState));
        Assert.Equal([ActualState.Unknown, ActualState.Unknown], states);
        Assert.Equal(result, ResultText(vm));
        Assert.Equal(saves, _paths.SaveCount);
        Assert.Empty(TempFiles());
    }

    [Fact(Timeout = 10_000)]
    public async Task AfterATimeout_UnreportedRowsAreUnknown_NotAccessDenied()
    {
        // Denied at inspect time too, so a re-inspection would read AccessDenied.
        _visibility.Set("/a", ActualState.AccessDenied);
        _visibility.Set("/b", ActualState.AccessDenied);
        var vm = ViewModel(Applicator(childTimeout: TimeSpan.FromMilliseconds(200)), null, "/a", "/b");
        await vm.ScanTask;
        _child.OnStart = run => Hide(run, "/a");

        await HideAll(vm);

        Assert.Equal(ActualState.Hidden, StateOf(vm, "/a"));
        Assert.Equal(ActualState.Unknown, StateOf(vm, "/b"));
        var result = Assert.Single(vm.OperationalResults);
        Assert.True(result.IsError);
        Assert.Equal("1 applied, 1 not responding", English.Of(result.Message));

        _child.Runs[0].Exit();
    }

    [Fact(Timeout = 10_000)]
    public async Task Quit_DuringTheElevatedWait_EndsWithinTheBound_AndTheFilesGoOnceTheChildExits()
    {
        var applicator = Applicator();
        var vm = ViewModel(applicator, TimeSpan.FromMilliseconds(300), "/a");
        await vm.ScanTask;

        var launch = _child.NextLaunch;
        var hide = HideAll(vm);
        var run = await launch;
        Assert.True(vm.HasWorkToFinish);

        var clock = Stopwatch.StartNew();
        var shutdown = vm.ShutdownAsync();
        Assert.Same(shutdown, vm.ShutdownAsync());
        await shutdown;
        await hide;

        Assert.True(clock.Elapsed < Generous);
        Assert.False(vm.IsApplying);
        Assert.Equal(ActualState.Unknown, StateOf(vm, "/a"));
        // The child outlived the bound, so its files wait for it (or for the next launch's sweep).
        Assert.NotEmpty(TempFiles());

        run.Exit();
        await applicator.ReleaseAsync(Generous);
        Assert.Empty(TempFiles());
    }

    [Fact(Timeout = 10_000)]
    public async Task Quit_WhenTheChildExitsWithinTheBound_RemovesItsFilesBeforeClosing()
    {
        var vm = ViewModel(Applicator(), Generous, "/a");
        await vm.ScanTask;

        var launch = _child.NextLaunch;
        var hide = HideAll(vm);
        var run = await launch;
        _ = Task.Delay(100, TestContext.Current.CancellationToken).ContinueWith(_ => run.Exit(), TaskScheduler.Default);

        await vm.ShutdownAsync();
        await hide;

        Assert.Empty(TempFiles());
    }

    [Fact(Timeout = 10_000)]
    public async Task ABatchPastTheCommandLineCap_ReachesTheChildWhole()
    {
        var paths = Enumerable.Range(0, 400)
            .Select(i => $@"C:\Users\u\{new string('x', 200)}\{i}")
            .ToList();
        Assert.True(paths.Sum(path => path.Length) > 32_767);
        _child.OnStart = run => run.Exit(0);

        var outcome = await Applicator().ApplyAsync(new ElevatedApplyCommand.Buckets(paths, [], []), TestContext.Current.CancellationToken);

        Assert.Equal(ElevatedApplyStatus.Completed, outcome.Status);
        var run = Assert.Single(_child.Runs);
        Assert.Equal(7, run.Arguments.Count);
        Assert.Equal(paths, run.Request.ToHide);
        Assert.Empty(TempFiles());
    }
}
