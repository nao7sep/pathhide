using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using Xunit;

namespace PathHide.Tests.Services;

public class PathScannerTests
{
    private static PathEntry Entry(string path) =>
        new() { Path = path, DesiredVisibility = DesiredVisibility.Hidden };

    private static async Task<List<ScanResult>> CollectAsync(
        PathScanner scanner,
        IReadOnlyList<PathEntry> entries,
        CancellationToken token = default)
    {
        var results = new List<ScanResult>();
        await foreach (var r in scanner.ScanAsync(entries, token))
            results.Add(r);
        return results;
    }

    [Fact]
    public async Task ScanAsync_YieldsOneResultPerEntry_InOrder()
    {
        var fake = new FakeVisibilityService();
        var scanner = new PathScanner(new BoundedVisibility(fake));
        var entries = new[] { Entry("/a"), Entry("/b"), Entry("/c") };

        var results = await CollectAsync(scanner, entries, token: TestContext.Current.CancellationToken);

        Assert.Equal(3, results.Count);
        Assert.Equal("/a", results[0].Entry.Path);
        Assert.Equal("/b", results[1].Entry.Path);
        Assert.Equal("/c", results[2].Entry.Path);
    }

    [Fact]
    public async Task ScanAsync_UnparseablePath_ReportsErrorWithoutInspecting()
    {
        var fake = new FakeVisibilityService();
        var scanner = new PathScanner(new BoundedVisibility(fake));

        var results = await CollectAsync(scanner, new[] { Entry("not-absolute") }, token: TestContext.Current.CancellationToken);

        var only = Assert.Single(results);
        Assert.Equal(ActualState.Error, only.Inspection.ActualState);
        Assert.Equal(ItemKind.Unknown, only.Inspection.ItemKind);
        Assert.Equal(default, only.Family);
        Assert.Empty(fake.Inspected); // Inspect must not be called for a path that doesn't parse.
    }

    [Fact]
    public async Task ScanAsync_ParseablePath_FlowsInspectionAndFamily()
    {
        var fake = new FakeVisibilityService();
        fake.Set("/x", ActualState.Hidden, ItemKind.Directory);
        var scanner = new PathScanner(new BoundedVisibility(fake));

        var results = await CollectAsync(scanner, new[] { Entry("/x") }, token: TestContext.Current.CancellationToken);

        var only = Assert.Single(results);
        Assert.Equal(ActualState.Hidden, only.Inspection.ActualState);
        Assert.Equal(ItemKind.Directory, only.Inspection.ItemKind);
        Assert.Equal(PathFamily.Posix, only.Family);
        Assert.Equal(new[] { "/x" }, fake.Inspected);
    }

    // A path that does not answer costs its own bound; the rows after it are inspected meanwhile.
    [Fact(Timeout = 10_000)]
    public async Task A_stalled_path_does_not_hold_back_the_results_after_it()
    {
        using var gate = new ManualResetEventSlim(initialState: false);
        var visibility = new FakeVisibilityService
        {
            OnInspect = path =>
            {
                if (path == "/stuck")
                    gate.Wait();
                return null;
            },
        };
        var scanner = new PathScanner(new BoundedVisibility(visibility));
        var token = TestContext.Current.CancellationToken;

        await using var results = scanner.ScanAsync([Entry("/stuck"), Entry("/b"), Entry("/c")], token).GetAsyncEnumerator(token);
        var arrived = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            Assert.True(await results.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token));
            arrived.Add(results.Current.Entry.Path);
        }
        gate.Set();

        Assert.Equal(["/b", "/c"], arrived.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ScanAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var fake = new FakeVisibilityService();
        var scanner = new PathScanner(new BoundedVisibility(fake));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
            () => CollectAsync(scanner, new[] { Entry("/a") }, token: cts.Token));
    }

    // Timed, because the failure mode is a HANG: without the fix the scan waits on the blocked
    // inspection forever, so an untimed test would stall the suite instead of reporting.
    [Fact(Timeout = 10_000)]
    public async Task Cancelling_AbandonsAnInspectionThatIsAlreadyBlocked()
    {
        // The token can only stop a work item from STARTING. Inspect is a blocking stat, and on
        // an unreachable UNC server or a stale mount it blocks for tens of seconds - so Cancel
        // did nothing observable until the current item returned, and every mutating command sat
        // dead behind it, since each pauses the scan and then awaits it with no timeout.
        using var gate = new ManualResetEventSlim(initialState: false);
        var visibility = new FakeVisibilityService { InspectGate = gate };
        var scanner = new PathScanner(new BoundedVisibility(visibility));
        using var cts = new CancellationTokenSource();

        // One path more than are inspected at once: the last is never started.
        List<PathEntry> entries = [.. Enumerable.Range(0, BoundedVisibility.Width).Select(i => Entry($"/blocked{i}")), Entry("/never-reached")];
        var scan = CollectAsync(scanner, entries, token: cts.Token);

        // Wait until the inspection is genuinely in-flight, then cancel while it is stuck.
        await visibility.InspectEntered.Task;
        await cts.CancelAsync();

        // The scan gives up on the wait rather than sitting on it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);

        // And the second path was never started.
        Assert.DoesNotContain("/never-reached", visibility.Inspected);

        gate.Set(); // let the abandoned probe finish on its own thread
    }
}
