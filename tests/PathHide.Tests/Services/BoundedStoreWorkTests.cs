using System;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class BoundedStoreWorkTests
{
    [Fact]
    public async Task Timeout_keeps_the_physical_tail_and_never_starts_a_conflicting_call()
    {
        var owner = new BoundedStoreWork();
        using var gate = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task? physical = null;
        var first = owner.RunAsync(() =>
        {
            Interlocked.Increment(ref calls);
            started.SetResult();
            gate.Wait();
            return 1;
        }, TimeSpan.FromMilliseconds(150), TimeProvider.System, TestContext.Current.CancellationToken, started: task => physical = task);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<TimeoutException>(() => first);
            await Assert.ThrowsAsync<TimeoutException>(() => owner.RunAsync(() =>
            {
                Interlocked.Increment(ref calls);
                return 2;
            }, TimeSpan.FromMilliseconds(150), TimeProvider.System, TestContext.Current.CancellationToken));
            Assert.Equal(1, calls);
        }
        finally
        {
            gate.Set();
            if (physical is not null)
                await physical.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        Assert.Equal(3, await owner.RunAsync(() => 3, TimeSpan.FromSeconds(2), TimeProvider.System, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_releases_the_wait_but_preserves_physical_ordering()
    {
        var owner = new BoundedStoreWork();
        using var gate = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        Task? physical = null;
        var first = owner.RunAsync(() =>
        {
            started.SetResult();
            gate.Wait();
            finished = true;
            return 1;
        }, TimeSpan.FromSeconds(2), TimeProvider.System, cancel.Token, started: task => physical = task);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            var next = owner.RunAsync(() => { Assert.True(finished); return 2; }, TimeSpan.FromSeconds(2), TimeProvider.System, TestContext.Current.CancellationToken);
            Assert.False(next.IsCompleted);
            gate.Set();
            Assert.Equal(2, await next);
        }
        finally
        {
            gate.Set();
            if (physical is not null)
                await physical.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
    }
}
