using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using Xunit;

namespace PathHide.Tests.Services;

// Timed throughout, because the failure mode is a HANG: an unbounded wait on a stalled path never
// returns, so an untimed test would stall the suite instead of failing.
[Collection(ProcessLogCollection.Name)]
public class BoundedVisibilityTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    // Where the system resolves no spelling, there is nothing to wait on: no call runs for it.
    [Fact]
    public async Task A_system_that_resolves_nothing_is_not_asked()
    {
        var service = new FakeVisibilityService { ResolvesPaths = false };

        Assert.Null(await new BoundedVisibility(service, Short).ResolvePathAsync("/a", TestContext.Current.CancellationToken));
        Assert.Empty(service.Resolved);
    }

    // A stuck call holds its thread for the network timeout; on the shared pool, enough of them would
    // delay the app's own continuations, saves and reads.
    [Fact(Timeout = 10_000)]
    public async Task A_file_system_call_runs_on_its_own_thread_not_the_shared_pool()
    {
        bool? onPool = null;
        var service = new FakeVisibilityService
        {
            OnInspect = _ =>
            {
                onPool = Thread.CurrentThread.IsThreadPoolThread;
                return null;
            },
        };

        await new BoundedVisibility(service, Short).InspectAsync("/a", TestContext.Current.CancellationToken);

        Assert.False(onPool);
    }

    [Theory(Timeout = 10_000)]
    [InlineData(@"C:\Temp\a", @"c:/TEMP/A/")]
    [InlineData(@"\\server\share\a", @"\\SERVER\SHARE\A")]
    [InlineData("/alias/a", "/real/a")]
    public async Task Equivalent_replacements_wait_for_the_outstanding_physical_write(string oldPath, string replacement)
    {
        using var release = new ManualResetEventSlim();
        var service = new FakeVisibilityService();
        if (oldPath.StartsWith('/'))
            service.Aliases[oldPath] = replacement;
        var visibility = new BoundedVisibility(service, Short);
        var token = TestContext.Current.CancellationToken;
        await visibility.ResolvePathAsync(oldPath, token); // Caller-observed admission evidence.
        service.WriteGate = release;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => visibility.WriteAsync(oldPath, DesiredVisibility.Hidden, WindowsHideMode.HiddenOnly, token));
            service.WriteGate = null; // A bypass would now write immediately.
            await Assert.ThrowsAsync<TimeoutException>(() => visibility.WriteAsync(replacement, DesiredVisibility.Shown, WindowsHideMode.HiddenOnly, token));
            Assert.Empty(service.Shown);
        }
        finally
        {
            release.Set();
            await visibility.InspectAsync(oldPath, token); // Join before disposing the fixture.
        }
        await visibility.WriteAsync(replacement, DesiredVisibility.Shown, WindowsHideMode.HiddenOnly, token);
        Assert.Equal([replacement], service.Shown);
    }

    [Fact(Timeout = 10_000)]
    public async Task Distinct_posix_case_and_final_link_identities_remain_independent()
    {
        using var release = new ManualResetEventSlim();
        var service = new FakeVisibilityService();
        service.Aliases["/link"] = "/link"; // A no-follow adapter retains the final link.
        service.Aliases["/target"] = "/target";
        var visibility = new BoundedVisibility(service, Short);
        var token = TestContext.Current.CancellationToken;
        await visibility.ResolvePathAsync("/link", token);
        await visibility.ResolvePathAsync("/target", token);
        service.WriteGate = release;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => visibility.WriteAsync("/Foo", DesiredVisibility.Hidden, WindowsHideMode.HiddenOnly, token));
            service.WriteGate = null;
            await visibility.WriteAsync("/foo", DesiredVisibility.Shown, WindowsHideMode.HiddenOnly, token);
            service.WriteGate = release;
            await Assert.ThrowsAsync<TimeoutException>(() => visibility.WriteAsync("/link", DesiredVisibility.Hidden, WindowsHideMode.HiddenOnly, token));
            service.WriteGate = null;
            await visibility.WriteAsync("/target", DesiredVisibility.Shown, WindowsHideMode.HiddenOnly, token);
            Assert.Equal(["/foo", "/target"], service.Shown);
        }
        finally
        {
            release.Set();
            await visibility.InspectAsync("/Foo", token);
            await visibility.InspectAsync("/link", token);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task Inspect_ThatNeverAnswers_IsReportedUnresponsive()
    {
        using var stalled = new ManualResetEventSlim(false);
        var service = new FakeVisibilityService { InspectGate = stalled };
        var visibility = new BoundedVisibility(service, Short);

        var inspection = await visibility.InspectAsync("/share/a", TestContext.Current.CancellationToken);

        Assert.Equal(ActualState.Unresponsive, inspection.ActualState);
        Assert.Equal(ItemKind.Unknown, inspection.ItemKind);
        stalled.Set();
    }

    [Fact(Timeout = 10_000)]
    public async Task Write_ThatNeverAnswers_TimesOut()
    {
        using var stalled = new ManualResetEventSlim(false);
        var service = new FakeVisibilityService { WriteGate = stalled };
        var visibility = new BoundedVisibility(service, Short);

        await Assert.ThrowsAsync<TimeoutException>(
            () => visibility.WriteAsync("/share/a", DesiredVisibility.Hidden, WindowsHideMode.HiddenOnly, TestContext.Current.CancellationToken));
        stalled.Set();
    }

    [Fact(Timeout = 10_000)]
    public async Task Cancelling_AbandonsACallThatIsAlreadyBlocked()
    {
        using var stalled = new ManualResetEventSlim(false);
        var service = new FakeVisibilityService { InspectGate = stalled };
        var visibility = new BoundedVisibility(service, TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();

        var inspect = visibility.InspectAsync("/share/a", cts.Token);
        await service.InspectEntered.Task;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspect);
        stalled.Set();
    }

    // The records say what was attempted on a path that did not answer, so a row left Unknown after a
    // write can be explained: which call, how long it was waited on, and what it did once it returned.
    [Fact(Timeout = 10_000)]
    public async Task An_abandoned_call_is_recorded_with_its_operation_and_its_return()
    {
        using var log = new StartupDiagnosticLog("path did not answer; abandoned the wait");
        using var stalled = new ManualResetEventSlim(false);
        var service = new FakeVisibilityService { WriteGate = stalled };
        var visibility = new BoundedVisibility(service, Short);
        var token = TestContext.Current.CancellationToken;
        const string path = "/abandoned/recorded";
        bool For(LogEntry entry, string message) =>
            entry.Message == message && entry.Fields?["path"]?.GetValue<string>() == path;

        await Assert.ThrowsAsync<TimeoutException>(
            () => visibility.WriteAsync(path, DesiredVisibility.Hidden, WindowsHideMode.HiddenOnly, token));
        await Assert.ThrowsAsync<TimeoutException>(
            () => visibility.WriteAsync(path, DesiredVisibility.Shown, WindowsHideMode.HiddenOnly, token));
        stalled.Set();

        var abandoned = await log.Find(entry => For(entry, "path did not answer; abandoned the wait")).WaitAsync(token);
        var refused = await log.Find(entry => For(entry, "path not called; an earlier call has not returned")).WaitAsync(token);
        var returned = await log.Find(entry => For(entry, "abandoned path call returned")).WaitAsync(token);
        Assert.Equal("hide", abandoned.Fields!["operation"]!.GetValue<string>());
        Assert.True(abandoned.Fields["waitedSeconds"]!.GetValue<double>() > 0);
        Assert.Equal("show", refused.Fields!["operation"]!.GetValue<string>());
        Assert.Equal("hide", returned.Fields!["operation"]!.GetValue<string>());
        Assert.True(returned.Fields["timedOut"]!.GetValue<bool>());
    }

    [Fact(Timeout = 10_000)]
    public async Task APathStillStuck_IsNotCalledAgain_UntilItsAbandonedCallReturns()
    {
        // A later Show must not race an earlier Hide still blocked on the same path, and repeated
        // commands must not pile blocked threads onto one dead share.
        using var stalled = new ManualResetEventSlim(false);
        var service = new FakeVisibilityService { WriteGate = stalled };
        var visibility = new BoundedVisibility(service, Short);
        var token = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<TimeoutException>(
            () => visibility.WriteAsync("/share/a", DesiredVisibility.Hidden, WindowsHideMode.HiddenOnly, token));

        // Still stuck: once the bound runs out waiting for it, reported without touching the path.
        var inspectedBefore = service.Inspected.Count;
        Assert.Equal(ActualState.Unresponsive, (await visibility.InspectAsync("/share/a", token)).ActualState);
        await Assert.ThrowsAsync<TimeoutException>(
            () => visibility.WriteAsync("/share/a", DesiredVisibility.Shown, WindowsHideMode.HiddenOnly, token));
        Assert.Equal(inspectedBefore, service.Inspected.Count);
        Assert.Empty(service.Shown);

        // Another path is unaffected.
        Assert.Equal(ActualState.Visible, (await visibility.InspectAsync("/local/b", token)).ActualState);

        // A call made while the write is still stuck waits for it, then runs: it sees what the
        // earlier write did rather than racing it.
        var afterRelease = visibility.InspectAsync("/share/a", token);
        stalled.Set();
        Assert.Equal(ActualState.Hidden, (await afterRelease).ActualState);
        Assert.Contains("/share/a", service.Inspected.ToArray());
    }
}
