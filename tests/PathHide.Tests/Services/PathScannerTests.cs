using System.Collections.Generic;
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
        System.IProgress<int>? progress = null,
        CancellationToken token = default)
    {
        var results = new List<ScanResult>();
        await foreach (var r in scanner.ScanAsync(entries, progress, token))
            results.Add(r);
        return results;
    }

    [Fact]
    public async Task ScanAsync_YieldsOneResultPerEntry_InOrder()
    {
        var fake = new FakeVisibilityService();
        var scanner = new PathScanner(fake);
        var entries = new[] { Entry("/a"), Entry("/b"), Entry("/c") };

        var results = await CollectAsync(scanner, entries);

        Assert.Equal(3, results.Count);
        Assert.Equal("/a", results[0].Entry.Path);
        Assert.Equal("/b", results[1].Entry.Path);
        Assert.Equal("/c", results[2].Entry.Path);
    }

    [Fact]
    public async Task ScanAsync_UnparseablePath_ReportsErrorWithoutInspecting()
    {
        var fake = new FakeVisibilityService();
        var scanner = new PathScanner(fake);

        var results = await CollectAsync(scanner, new[] { Entry("not-absolute") });

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
        var scanner = new PathScanner(fake);

        var results = await CollectAsync(scanner, new[] { Entry("/x") });

        var only = Assert.Single(results);
        Assert.Equal(ActualState.Hidden, only.Inspection.ActualState);
        Assert.Equal(ItemKind.Directory, only.Inspection.ItemKind);
        Assert.Equal(PathFamily.Posix, only.Family);
        Assert.Equal(new[] { "/x" }, fake.Inspected);
    }

    [Fact]
    public async Task ScanAsync_ReportsProgressIncrementally()
    {
        var fake = new FakeVisibilityService();
        var scanner = new PathScanner(fake);
        var reported = new List<int>();
        // Synchronous IProgress avoids the SynchronizationContext post used by Progress<T>.
        var progress = new SynchronousProgress<int>(reported.Add);

        await CollectAsync(scanner, new[] { Entry("/a"), Entry("/b"), Entry("/c") }, progress);

        Assert.Equal(new[] { 1, 2, 3 }, reported);
    }

    [Fact]
    public async Task ScanAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var fake = new FakeVisibilityService();
        var scanner = new PathScanner(fake);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
            () => CollectAsync(scanner, new[] { Entry("/a") }, token: cts.Token));
    }

    private sealed class SynchronousProgress<T> : System.IProgress<T>
    {
        private readonly System.Action<T> _handler;
        public SynchronousProgress(System.Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
