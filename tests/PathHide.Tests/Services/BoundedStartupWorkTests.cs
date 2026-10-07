using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Services;
using PathHide.Tests.Fakes;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class BoundedStartupWorkTests
{
    [Fact]
    public async Task A_required_preparation_fault_after_timeout_retains_its_full_diagnostic_cause()
    {
        using var release = new ManualResetEventSlim();
        using var diagnostics = new StartupDiagnosticLog("startup: abandoned preparation failed");
        var work = BoundedStartupWork.RunAsync<int>(() =>
        {
            release.Wait();
            throw new IOException("late I/O sentinel", new InvalidOperationException("late cause sentinel"));
        }, bound: TimeSpan.FromMilliseconds(100), cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => work);
            Assert.False(diagnostics.Reported.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            var report = await diagnostics.Reported.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.Equal("warn", report.Level);
            var error = report.Error?.ToJsonString();
            Assert.Contains("IOException", error, StringComparison.Ordinal);
            Assert.Contains("late I/O sentinel", error, StringComparison.Ordinal);
            Assert.Contains("late cause sentinel", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Cancellation_disposes_a_late_prepared_resource_exactly_once()
    {
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleans = 0;
        var work = BoundedStartupWork.RunAsync(() =>
        {
            began.SetResult();
            release.Wait();
            return 1;
        }, abandoned: _ =>
        {
            Interlocked.Increment(ref cleans);
            cleaned.TrySetResult();
        }, bound: TimeSpan.FromSeconds(2), cancellationToken: cancel.Token);
        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            Assert.Equal(0, cleans);
        }
        finally
        {
            release.Set();
            await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        Assert.Equal(1, cleans);
    }
}
