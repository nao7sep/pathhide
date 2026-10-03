using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
            new BoundedVisibility(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState())
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

    private sealed class HeldApplicator : IElevatedApplicator
    {
        public bool HasRunningChild { get; set; }

        public TaskCompletionSource Release { get; } = new();

        public int Releases { get; private set; }

        public Task<ElevatedApplyOutcome> ApplyAsync(ElevatedApplyCommand.Buckets buckets, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(TimeSpan bound)
        {
            Releases++;
            return Release.Task;
        }
    }
}
