using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Services;
using PathHide.Tests.Fakes;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class ElevatedApplicatorTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("pathhide-elevated-owner-").FullName;
    private static readonly ElevatedApplyCommand.Buckets Buckets = new(["/a"], [], []);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public async Task A_GUI_launch_always_carries_its_process_identity_and_session()
    {
        ElevatedApplyCommand.Invocation? invocation = null;
        var applicator = new ElevatedApplicator(arguments =>
        {
            invocation = ElevatedApplyCommand.ParseArguments(arguments);
            return Task.FromResult<Task<int>?>(Task.FromResult(0));
        }, _temp, "/storage-root");
        Assert.Equal(ElevatedApplyStatus.Completed,
            (await applicator.ApplyAsync(Buckets, CancellationToken.None)).Status);
        Assert.Equal(ElevatedApplyCommand.Parent.Current, invocation!.Parent);
        Assert.Equal(Log.Session, PathHide.Storage.FileTimestamp.SerializedStamp(invocation.Session!.Value));
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Abandoned_preparation_keeps_ownership_and_never_launches_later(bool cancel)
    {
        using var release = new ManualResetEventSlim();
        using var stop = new CancellationTokenSource();
        var entered = Signal();
        var launches = 0;
        var applicator = new ElevatedApplicator(_ =>
        {
            Interlocked.Increment(ref launches);
            return Task.FromResult<Task<int>?>(Task.FromResult(0));
        }, _temp, "/storage-root")
        {
            FileWaitBound = cancel ? Generous : TimeSpan.FromMilliseconds(100),
            WriteRequest = (path, content) =>
            {
                entered.SetResult();
                release.Wait();
                File.WriteAllBytes(path, content);
            },
        };
        var apply = applicator.ApplyAsync(Buckets, stop.Token);
        try
        {
            await entered.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);
            if (cancel)
                await stop.CancelAsync();
            var outcome = await apply.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.Equal(cancel ? ElevatedApplyStatus.Cancelled : ElevatedApplyStatus.NotStarted, outcome.Status);
            Assert.True(applicator.HasRunningChild); // The physical preparation still owns its files.
            Assert.Equal(ElevatedApplyStatus.Busy,
                (await applicator.ApplyAsync(Buckets, CancellationToken.None)).Status);
            await applicator.ReleaseAsync(TimeSpan.Zero);
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set();
            await apply;
            await applicator.ReleaseAsync(Generous);
        }
        Assert.Equal(0, launches);
        Assert.False(applicator.HasRunningChild);
        Assert.Empty(Directory.GetFiles(_temp));
    }

    [Fact(Timeout = 10_000)]
    public async Task An_unavailable_report_ends_the_foreground_wait_but_owns_read_and_cleanup()
    {
        using var release = new ManualResetEventSlim();
        var entered = Signal();
        var child = new FakeElevatedChild { OnStart = run => { run.Report("/a"); run.Exit(); } };
        var reads = 0;
        var applicator = new ElevatedApplicator(child.LaunchAsync, _temp, "/storage-root")
        {
            FileWaitBound = TimeSpan.FromMilliseconds(100),
            ReadResults = files =>
            {
                Interlocked.Increment(ref reads);
                entered.SetResult();
                release.Wait();
                return files.ReadResults();
            },
        };
        var apply = applicator.ApplyAsync(Buckets, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);
            var outcome = await apply.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.Equal(ElevatedApplyStatus.Completed, outcome.Status);
            Assert.Empty(outcome.Results); // Late evidence cannot revise this response.
            Assert.True(applicator.HasRunningChild);
        }
        finally
        {
            release.Set();
            await apply;
            await applicator.ReleaseAsync(Generous);
        }
        Assert.Equal(1, reads);
        Assert.Empty(Directory.GetFiles(_temp));
    }

    [Fact(Timeout = 10_000)]
    public async Task Delayed_cleanup_preserves_completed_verdicts_and_runs_once()
    {
        using var release = new ManualResetEventSlim();
        var entered = Signal();
        var child = new FakeElevatedChild { OnStart = run => { run.Report("/a"); run.Exit(); } };
        var deletions = 0;
        var applicator = new ElevatedApplicator(child.LaunchAsync, _temp, "/storage-root")
        {
            FileWaitBound = TimeSpan.FromMilliseconds(100),
            DeleteFiles = files =>
            {
                Interlocked.Increment(ref deletions);
                entered.SetResult();
                release.Wait();
                return files.TryDelete();
            },
        };
        var apply = applicator.ApplyAsync(Buckets, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);
            var outcome = await apply.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.Equal(ElevatedApplyStatus.Completed, outcome.Status);
            Assert.True(outcome.Results["/a"]);
            Assert.True(applicator.HasRunningChild);
            await applicator.ReleaseAsync(TimeSpan.Zero);
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set();
            await apply;
            await applicator.ReleaseAsync(Generous);
        }
        Assert.Equal(1, deletions);
        Assert.Empty(Directory.GetFiles(_temp));
    }
}

/// <summary>
/// A child that ends badly may not have reported anything itself, so the parent's record of its exit
/// carries the severity.
/// </summary>
[Collection(ProcessLogCollection.Name)]
public sealed class ElevatedExitRecordTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("pathhide-elevated-exit-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Theory]
    [InlineData(0, "info")]
    [InlineData(1, "warn")]
    [InlineData(3, "warn")]
    public async Task The_exit_is_a_warning_unless_the_child_succeeded(int exitCode, string level)
    {
        using var log = new StartupDiagnosticLog("elevated apply: exited");
        var applicator = new ElevatedApplicator(
            _ => Task.FromResult<Task<int>?>(Task.FromResult(exitCode)), _temp, "/storage-root");

        await applicator.ApplyAsync(new(["/a"], [], []), CancellationToken.None);

        var exited = await log.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(level, exited.Level);
        Assert.Equal(exitCode, exited.Fields!["exitCode"]!.GetValue<int>());
    }
}
