using System;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class StartupDiagnosticTests
{
    [Fact]
    public async Task Reporting_returns_to_startup_while_its_diagnostic_sink_is_stalled()
    {
        using var release = new ManualResetEventSlim();
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new HeldSink(began, release);
        using var logger = new SessionLogger(DateTimeOffset.UtcNow, sink, fallbackDirectory: null,
            debugEnabled: false, writeInBackground: false);
        var report = Log.ReportStartup("startup: test failure", new InvalidOperationException("diagnostic sentinel"), logger);
        try
        {
            await began.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(report.IsCompleted);
        }
        finally
        {
            release.Set();
            await report.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        Assert.Equal("startup: test failure", sink.Stored?.Message);
        Assert.Contains("diagnostic sentinel", sink.Stored?.Error?.ToJsonString(), StringComparison.Ordinal);
    }

    private sealed class HeldSink(TaskCompletionSource began, ManualResetEventSlim release) : ILogSink
    {
        public LogEntry? Stored { get; private set; }
        public void Write(LogEntry entry)
        {
            began.TrySetResult();
            release.Wait();
            Stored = entry;
        }
        public void Dispose() { }
    }
}
