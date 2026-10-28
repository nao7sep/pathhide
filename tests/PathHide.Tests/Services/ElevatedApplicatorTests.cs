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
        }, _temp, "/storage-root") { BindWindowsTargets = false };
        Assert.Equal(ElevatedApplyStatus.Completed,
            (await applicator.ApplyAsync(Buckets, CancellationToken.None)).Status);
        Assert.Equal(ElevatedApplyCommand.Parent.Current, invocation!.Parent);
        Assert.Equal(Log.Session, PathHide.Storage.FileTimestamp.SerializedStamp(invocation.Session!.Value));
    }

    // The approval prompt cannot be taken back, so a cancel while it is open takes back the request: a
    // child the user approves afterwards finds nothing to apply.
    [Fact(Timeout = 10_000)]
    public async Task Cancelling_while_the_prompt_is_open_withdraws_the_request()
    {
        var prompt = new TaskCompletionSource<Task<int>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? requestPath = null;
        var prompted = Signal();
        var applicator = new ElevatedApplicator(arguments =>
        {
            requestPath = ElevatedApplyCommand.ParseArguments(arguments)!.RequestPath;
            prompted.SetResult();
            return prompt.Task;
        }, _temp, "/storage-root") { BindWindowsTargets = false };
        using var stop = new CancellationTokenSource();

        var apply = applicator.ApplyAsync(Buckets, stop.Token);
        await prompted.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(requestPath));
        await stop.CancelAsync();

        Assert.Equal(ElevatedApplyStatus.Cancelled, (await apply.WaitAsync(Generous, TestContext.Current.CancellationToken)).Status);
        Assert.False(File.Exists(requestPath));
        prompt.SetResult(Task.FromResult(0));
        await applicator.ReleaseAsync(Generous);
    }

    [Fact]
    public async Task A_preparation_cancelled_while_it_writes_never_launches()
    {
        using var stop = new CancellationTokenSource();
        var launches = 0;
        var applicator = new ElevatedApplicator(_ =>
        {
            launches++;
            return Task.FromResult<Task<int>?>(Task.FromResult(0));
        }, _temp, "/storage-root")
        {
            BindWindowsTargets = false,
            WriteRequest = (path, content) =>
            {
                stop.Cancel();
                File.WriteAllBytes(path, content);
            },
        };

        var outcome = await applicator.ApplyAsync(Buckets, stop.Token);
        await applicator.Settled;

        Assert.Equal(ElevatedApplyStatus.Cancelled, outcome.Status);
        Assert.Equal(0, launches);
        Assert.Empty(Directory.GetFiles(_temp));
    }

    [Fact]
    public async Task A_child_still_running_after_a_cancel_keeps_the_operation_unsettled_until_it_exits()
    {
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var applicator = new ElevatedApplicator(_ => Task.FromResult<Task<int>?>(exit.Task), _temp, "/storage-root")
        { BindWindowsTargets = false };
        var apply = applicator.ApplyAsync(Buckets, stop.Token);
        await stop.CancelAsync();

        Assert.Equal(ElevatedApplyStatus.Cancelled, (await apply).Status);
        Assert.False(applicator.Settled.IsCompleted);
        Assert.Equal(ElevatedApplyStatus.Busy, (await applicator.ApplyAsync(Buckets, CancellationToken.None)).Status);

        exit.SetResult(0);
        await applicator.Settled;
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
            _ => Task.FromResult<Task<int>?>(Task.FromResult(exitCode)), _temp, "/storage-root") { BindWindowsTargets = false };

        await applicator.ApplyAsync(new(["/a"], [], []), CancellationToken.None);

        var exited = await log.Reported.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(level, exited.Level);
        Assert.Equal(exitCode, exited.Fields!["exitCode"]!.GetValue<int>());
    }
}
