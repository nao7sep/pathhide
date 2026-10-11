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
public class BoundedVisibilityTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

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
            await Assert.ThrowsAsync<TimeoutException>(() => visibility.WriteAsync(oldPath, DesiredVisibility.Hidden, token));
            service.WriteGate = null; // A bypass would now write immediately.
            await Assert.ThrowsAsync<TimeoutException>(() => visibility.WriteAsync(replacement, DesiredVisibility.Shown, token));
            Assert.Empty(service.Shown);
        }
        finally
        {
            release.Set();
            await visibility.InspectAsync(oldPath, token); // Join before disposing the fixture.
        }
        await visibility.WriteAsync(replacement, DesiredVisibility.Shown, token);
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
            await Assert.ThrowsAsync<TimeoutException>(() => visibility.WriteAsync("/Foo", DesiredVisibility.Hidden, token));
            service.WriteGate = null;
            await visibility.WriteAsync("/foo", DesiredVisibility.Shown, token);
            service.WriteGate = release;
            await Assert.ThrowsAsync<TimeoutException>(() => visibility.WriteAsync("/link", DesiredVisibility.Hidden, token));
            service.WriteGate = null;
            await visibility.WriteAsync("/target", DesiredVisibility.Shown, token);
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
            () => visibility.WriteAsync("/share/a", DesiredVisibility.Hidden, TestContext.Current.CancellationToken));
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
            () => visibility.WriteAsync("/share/a", DesiredVisibility.Hidden, token));

        // Still stuck: once the bound runs out waiting for it, reported without touching the path.
        var inspectedBefore = service.Inspected.Count;
        Assert.Equal(ActualState.Unresponsive, (await visibility.InspectAsync("/share/a", token)).ActualState);
        await Assert.ThrowsAsync<TimeoutException>(
            () => visibility.WriteAsync("/share/a", DesiredVisibility.Shown, token));
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
