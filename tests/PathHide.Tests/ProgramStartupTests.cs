using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Services;
using PathHide.Tests.Fakes;
using Xunit;

namespace PathHide.Tests;

public sealed class ProgramStartupTests
{
    [Fact]
    public async Task A_late_records_failure_result_is_diagnosed_even_without_a_sink()
    {
        using var release = new ManualResetEventSlim();
        using var diagnostics = new StartupDiagnosticLog("startup: abandoned records open failed");
        try
        {
            var opened = Program.OpenRecordsForStartup(() =>
            {
                release.Wait();
                return (null, new IOException("late records sentinel", new InvalidOperationException("records cause sentinel")));
            }, TimeSpan.FromMilliseconds(100));
            Assert.Null(opened.Store);
            Assert.IsType<TimeoutException>(opened.Failure);
            Assert.False(diagnostics.Reported.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            var report = await diagnostics.Reported.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.Equal("warn", report.Level);
            var error = report.Error?.ToJsonString();
            Assert.Contains("IOException", error, StringComparison.Ordinal);
            Assert.Contains("late records sentinel", error, StringComparison.Ordinal);
            Assert.Contains("records cause sentinel", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_late_records_open_is_closed_instead_of_handed_to_the_logger()
    {
        using var release = new ManualResetEventSlim();
        var sink = new TrackedSink();
        try
        {
            var opened = Program.OpenRecordsForStartup(() =>
            {
                release.Wait();
                return (sink, null);
            }, TimeSpan.FromMilliseconds(100));
            Assert.Null(opened.Store);
            Assert.IsType<TimeoutException>(opened.Failure);
            Assert.Equal(0, sink.Closes);
        }
        finally
        {
            release.Set();
            await sink.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        Assert.Equal(1, sink.Closes);
    }

    [Fact]
    public void A_timely_records_open_transfers_ownership_without_closing_it()
    {
        var sink = new TrackedSink();
        try
        {
            var opened = Program.OpenRecordsForStartup(() => (sink, null), TimeSpan.FromSeconds(2));
            Assert.Same(sink, opened.Store);
            Assert.Null(opened.Failure);
            Assert.Equal(0, sink.Closes);
        }
        finally { sink.Dispose(); }
    }

    private sealed class TrackedSink : ILogSink
    {
        public int Closes;
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Write(LogEntry entry) => throw new NotSupportedException();
        public void Dispose()
        {
            Interlocked.Increment(ref Closes);
            Closed.TrySetResult();
        }
    }
}
