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
    public async Task ScanAsync_YieldsOneResultPerEntryInOrder()
    {
        var fake = new FakeVisibilityService();
        var scanner = new PathScanner(new VisibilityCalls(fake));
        var entries = new[] { Entry("/a"), Entry("/b"), Entry("/c") };

        var results = await CollectAsync(scanner, entries, token: TestContext.Current.CancellationToken);

        Assert.Equal(["/a", "/b", "/c"], results.Select(result => result.Entry.Path));
    }

    [Fact]
    public async Task ScanAsync_UnparseablePath_ReportsErrorWithoutInspecting()
    {
        var fake = new FakeVisibilityService();
        var scanner = new PathScanner(new VisibilityCalls(fake));

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
        var scanner = new PathScanner(new VisibilityCalls(fake));

        var results = await CollectAsync(scanner, new[] { Entry("/x") }, token: TestContext.Current.CancellationToken);

        var only = Assert.Single(results);
        Assert.Equal(ActualState.Hidden, only.Inspection.ActualState);
        Assert.Equal(ItemKind.Directory, only.Inspection.ItemKind);
        Assert.Equal(PathFamily.Posix, only.Family);
        Assert.Equal(new[] { "/x" }, fake.Inspected);
    }

    [Fact]
    public async Task ScanAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var fake = new FakeVisibilityService();
        var scanner = new PathScanner(new VisibilityCalls(fake));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
            () => CollectAsync(scanner, new[] { Entry("/a") }, token: cts.Token));
    }

    // An inspection is a blocking stat nothing can interrupt, so a cancel stops the scan before the next one.
    [Fact]
    public async Task Cancelling_stops_before_the_next_entry()
    {
        using var gate = new ManualResetEventSlim(initialState: false);
        var visibility = new FakeVisibilityService { InspectGate = gate };
        var scanner = new PathScanner(new VisibilityCalls(visibility));
        using var cts = new CancellationTokenSource();

        var scan = CollectAsync(scanner, [Entry("/blocked"), Entry("/never-reached")], token: cts.Token);
        await visibility.InspectEntered.Task;
        await cts.CancelAsync();
        gate.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.DoesNotContain("/never-reached", visibility.Inspected);
    }
}
