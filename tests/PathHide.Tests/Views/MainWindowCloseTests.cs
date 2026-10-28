using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>Closing the main window, which is how the app quits: work in flight finishes first.</summary>
public sealed class MainWindowCloseTests : WindowTest
{
    [AvaloniaFact]
    public void A_second_close_while_shutdown_still_runs_waits_for_it()
    {
        var elevated = new HeldApplicator { HasRunningChild = true };
        var settings = new FakeSettingsStore();
        var vm = new MainWindowViewModel(
            new VisibilityCalls(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), [])
        {
            ElevatedApplicator = elevated,
        };
        var main = Show(new MainWindow { DataContext = vm });
        var closed = false;
        main.Closed += (_, _) => closed = true;

        main.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(closed);

        // The child has gone, so nothing counts as busy any more, but releasing it has not returned:
        // a second quit must not let the window, and with it the process, go before it does.
        elevated.HasRunningChild = false;
        main.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(closed);

        elevated.Release.SetResult();
        var clock = Stopwatch.StartNew();
        while (!closed)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The window did not close once shutdown finished.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.Equal(1, elevated.Releases);
    }

    [AvaloniaFact]
    public void A_quit_called_off_after_a_failed_save_keeps_the_window_and_a_later_close_closes_it()
    {
        using var gate = new ManualResetEventSlim(false);
        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value = [new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }],
        };
        var settings = new FakeSettingsStore();
        var vm = new MainWindowViewModel(
            new VisibilityCalls(new FakeVisibilityService()), paths, settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), paths.Load().Value);
        var main = Show(new MainWindow { DataContext = vm });
        vm.ConfirmDestructiveAsync = _ => Task.FromResult(true);
        var asked = 0;
        vm.AskUnsavedAtQuitAsync = _ =>
        {
            asked++;
            return Task.FromResult(UnsavedQuitChoice.KeepOpen);
        };
        var closed = false;
        main.Closed += (_, _) => closed = true;
        WaitFor(() => vm.ScanTask.IsCompleted);

        // A Remove whose save is running and will fail.
        paths.SaveGate = gate;
        paths.ThrowOnSave = true;
        vm.Rows[0].IsSelected = true;
        _ = ((IAsyncRelayCommand)vm.RemoveSelectedCommand).ExecuteAsync(null);

        main.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(closed);

        gate.Set();
        WaitFor(() => asked == 1);
        Dispatcher.UIThread.RunJobs();
        Assert.False(closed);

        main.Close();
        WaitFor(() => closed);
        Assert.Equal(1, asked);
    }

    [AvaloniaFact]
    public void Closing_the_main_window_closes_Records_with_it()
    {
        var settings = new FakeSettingsStore();
        var vm = new MainWindowViewModel(
            new VisibilityCalls(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), []);
        var main = Show(new MainWindow { DataContext = vm });
        main.RecordsReader = () => new FakeRecordsReader();
        var records = Track(main.OpenRecords());
        Dispatcher.UIThread.RunJobs();

        main.Close();
        WaitFor(() => !main.IsVisible);

        Assert.False(records.IsVisible);
    }

    [AvaloniaFact]
    public void A_question_raised_while_the_main_window_is_minimized_stands_alone_and_leaves_it_minimized()
    {
        var settings = new FakeSettingsStore();
        var vm = new MainWindowViewModel(
            new VisibilityCalls(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), []);
        var main = Show(new MainWindow { DataContext = vm });
        main.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();

        // Every dialog opens through ShowBoundedAsync, so any one stands for the quit's questions.
        var question = new AboutDialog(_ => true);
        var shown = question.ShowBoundedAsync(main);
        Track(question);
        Dispatcher.UIThread.RunJobs();

        Assert.True(question.IsVisible);
        Assert.Null(question.Owner);
        Assert.Equal(WindowState.Minimized, main.WindowState);
        question.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(shown.IsCompleted);
    }

    // The operating system ending the session: these deliver its close into the window's own handlers,
    // as the platform does at logout, restart or shutdown, without logging out for real.

    [AvaloniaFact]
    public void The_session_ending_closes_the_window_within_the_bound_without_asking_while_a_save_stalls()
    {
        using var gate = new ManualResetEventSlim(false);
        var (vm, paths) = CreateWithOnePath(TimeSpan.FromMilliseconds(300));
        var main = Show(new MainWindow { DataContext = vm });
        vm.ConfirmDestructiveAsync = _ => Task.FromResult(true);
        var asked = 0;
        vm.AskUnsavedAtQuitAsync = _ =>
        {
            asked++;
            return Task.FromResult(UnsavedQuitChoice.KeepOpen);
        };
        var closed = false;
        main.Closed += (_, _) => closed = true;
        WaitFor(() => vm.ScanTask.IsCompleted);

        // A Remove whose save never lands while the session ends.
        paths.SaveGate = gate;
        vm.Rows[0].IsSelected = true;
        _ = ((IAsyncRelayCommand)vm.RemoveSelectedCommand).ExecuteAsync(null);
        Assert.True(vm.HasWorkToFinish);

        var clock = Stopwatch.StartNew();
        EndSession(main);

        // The close answers the system itself: it is not cancelled to wait elsewhere, and it is over
        // within the quit's bound plus the placement save's.
        Assert.True(closed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"The session-end close took {clock.Elapsed}.");
        Assert.Equal(0, asked);
        gate.Set();
    }

    [AvaloniaFact]
    public void The_session_ending_while_the_unsaved_question_is_open_closes_the_window_and_asks_no_more()
    {
        using var gate = new ManualResetEventSlim(false);
        var (vm, paths) = CreateWithOnePath(TimeSpan.FromMilliseconds(300));
        var main = Show(new MainWindow { DataContext = vm });
        vm.ConfirmDestructiveAsync = _ => Task.FromResult(true);
        var question = new TaskCompletionSource<UnsavedQuitChoice>();
        var asked = 0;
        vm.AskUnsavedAtQuitAsync = _ =>
        {
            asked++;
            return question.Task;
        };
        var closed = 0;
        main.Closed += (_, _) => closed++;
        WaitFor(() => vm.ScanTask.IsCompleted);

        // A Remove whose save fails, so the user's quit stops and asks.
        paths.SaveGate = gate;
        paths.ThrowOnSave = true;
        vm.Rows[0].IsSelected = true;
        _ = ((IAsyncRelayCommand)vm.RemoveSelectedCommand).ExecuteAsync(null);
        main.Close();
        gate.Set();
        WaitFor(() => asked == 1);
        Assert.Equal(0, closed);

        EndSession(main);
        Assert.Equal(1, closed);

        // The question's late answer neither asks again nor closes the window a second time.
        question.SetResult(UnsavedQuitChoice.Retry);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, asked);
        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public void The_session_ending_gives_a_stalled_elevated_child_only_the_bound()
    {
        var elevated = new HeldApplicator { HasRunningChild = true, Bounded = true };
        var settings = new FakeSettingsStore();
        var vm = new MainWindowViewModel(
            new VisibilityCalls(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), [])
        {
            ElevatedApplicator = elevated,
            ShutdownBound = TimeSpan.FromMilliseconds(300),
        };
        var main = Show(new MainWindow { DataContext = vm });
        var closed = false;
        main.Closed += (_, _) => closed = true;

        var clock = Stopwatch.StartNew();
        EndSession(main);

        Assert.True(closed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"The session-end close took {clock.Elapsed}.");
        Assert.Equal(1, elevated.Releases);
        elevated.Release.SetResult();
    }

    private static (MainWindowViewModel Vm, FakeJsonStore<List<PathEntry>> Paths) CreateWithOnePath(TimeSpan shutdownBound)
    {
        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value = [new PathEntry { Path = "/a", DesiredVisibility = DesiredVisibility.Hidden }],
        };
        var settings = new FakeSettingsStore();
        var vm = new MainWindowViewModel(
            new VisibilityCalls(new FakeVisibilityService()), paths, settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), paths.Load().Value)
        {
            ShutdownBound = shutdownBound,
        };
        return (vm, paths);
    }

    // The close Avalonia makes when the operating system ends the session, through its own close path
    // (internal, so reached by reflection), so every closing handler runs as it would at logout.
    private static void EndSession(Window window)
    {
        var closeCore = typeof(Window).GetMethod(
            "CloseCore", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(WindowCloseReason), typeof(bool), typeof(bool)]);
        Assert.NotNull(closeCore);
        closeCore.Invoke(window, [WindowCloseReason.OSShutdown, false, false]);
    }

    private static void WaitFor(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "The condition did not hold in time.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private sealed class HeldApplicator : IElevatedApplicator
    {
        public bool HasRunningChild { get; set; }

        public TaskCompletionSource Release { get; } = new();

        public Task Settled => Release.Task;

        public int Releases { get; private set; }

        // Whether the release keeps to its bound, as ElevatedApplicator's does, or holds until Release.
        public bool Bounded { get; init; }

        public Task<ElevatedApplyOutcome> ApplyAsync(ElevatedApplyCommand.Buckets buckets, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(TimeSpan bound)
        {
            Releases++;
            return Bounded ? Task.WhenAny(Release.Task, Task.Delay(bound)) : Release.Task;
        }
    }
}
