using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PathHide.Services;

/// <summary>How one elevated apply ended, as far as the caller is concerned.</summary>
public enum ElevatedApplyStatus
{
    /// <summary>The child ran and exited; <see cref="ElevatedApplyOutcome.Results"/> is its full report.</summary>
    Completed,

    /// <summary>No child ran: the consent prompt was declined, or the launch failed.</summary>
    NotStarted,

    /// <summary>The user cancelled the wait. The child, if it started, finishes on its own.</summary>
    Cancelled,

    /// <summary>The child did not exit in time. It keeps running; unreported paths may still change.</summary>
    TimedOut,

    /// <summary>Refused: a child from an earlier apply is still running and could overwrite this one.</summary>
    Busy,
}

/// <summary>
/// The result of one elevated apply: how it ended, the child's exit code when it exited while
/// watched, and the per-path outcomes it reported. A path absent from <see cref="Results"/> was never
/// reported on, and <see cref="Status"/> says what that means.
/// </summary>
public sealed record ElevatedApplyOutcome(
    ElevatedApplyStatus Status,
    int? ExitCode,
    IReadOnlyDictionary<string, bool> Results);

/// <summary>Runs access-denied paths through one elevated child process.</summary>
public interface IElevatedApplicator
{
    /// <summary>Whether a child launched earlier is still running after its caller stopped waiting.</summary>
    bool HasRunningChild { get; }

    /// <summary>
    /// Applies <paramref name="buckets"/> through an elevated child, or refuses with
    /// <see cref="ElevatedApplyStatus.Busy"/> while an earlier child still runs. Returns once; nothing
    /// the child does after that reaches the caller.
    /// </summary>
    Task<ElevatedApplyOutcome> ApplyAsync(ElevatedApplyCommand.Buckets buckets, CancellationToken cancellationToken);

    /// <summary>
    /// For quitting: waits up to <paramref name="bound"/> for a still-running child to exit so its temp
    /// files can be removed now; a child that outlives the bound leaves them for the next launch.
    /// </summary>
    Task ReleaseAsync(TimeSpan bound);
}

/// <summary>
/// Launches the elevated child with <paramref name="arguments"/>. The returned task completes once the
/// launch is settled (on Windows, once the user answers the consent prompt): null when no child
/// started, otherwise a task that completes with the child's exit code when it exits.
/// </summary>
public delegate Task<Task<int>?> ElevatedChildLauncher(IReadOnlyList<string> arguments);

/// <summary>
/// The platform-neutral half of the elevated retry: writes the request, launches the child through an
/// <see cref="ElevatedChildLauncher"/>, bounds and cancels the wait, reads the results, and owns the
/// temp files and the one child that may outlive a call.
/// </summary>
/// <remarks>
/// <para>An elevated child cannot be stopped from here — a higher-integrity process is not ours to signal —
/// so a cancel or a timeout only ends the wait. At most one child runs: while one is outstanding, a new
/// apply is refused instead of launched, because two children writing the same paths let the older
/// one's late write undo the newer one (hide a path the user has just shown) behind the list's back.</para>
/// <para>That refusal covers one GUI only. The child holds no lease and applies the absolute states of its
/// request snapshot without reading <c>paths.json</c>, and a quit waits for it only within the shutdown
/// bound. So a child still running after a quit can undo what a restarted PathHide has since applied to
/// the same paths. This is accepted (developer decision): <c>paths.json</c> keeps the newer desired
/// state, the next scan shows the difference, and applying again repairs it; no lock, IPC or child-side
/// recheck is added for it.</para>
/// <para>Every await here resumes on the thread pool: reading the results and removing the temp files
/// touch the OS temp volume, which antivirus or a slow disk can stall, so none of it runs on the
/// caller's UI thread.</para>
/// </remarks>
public sealed class ElevatedApplicator : IElevatedApplicator
{
    /// <summary>
    /// How long a started child may run before the caller stops waiting. It covers a whole batch on
    /// possibly slow storage, so it ends a wedge (a share that stopped answering mid-write) rather than
    /// policing a large healthy apply. The consent prompt is not counted: that wait is the user's.
    /// </summary>
    public static readonly TimeSpan DefaultChildTimeout = TimeSpan.FromMinutes(5);

    private static readonly IReadOnlyDictionary<string, bool> EmptyResults =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    private readonly ElevatedChildLauncher _launch;
    private readonly string _tempDirectory;
    private readonly string _storageRoot;
    private readonly TimeSpan _childTimeout;

    // The launch-to-cleanup lifetime of the newest child. Completed when none is outstanding. Read and
    // replaced only by ApplyAsync, which its caller serializes.
    private Task _outstanding = Task.CompletedTask;

    public ElevatedApplicator(
        ElevatedChildLauncher launch,
        string tempDirectory,
        string storageRoot,
        TimeSpan? childTimeout = null)
    {
        _launch = launch;
        _tempDirectory = tempDirectory;
        _storageRoot = storageRoot;
        _childTimeout = childTimeout ?? DefaultChildTimeout;
    }

    public bool HasRunningChild => !_outstanding.IsCompleted;

    public async Task<ElevatedApplyOutcome> ApplyAsync(
        ElevatedApplyCommand.Buckets buckets, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new ElevatedApplyOutcome(ElevatedApplyStatus.Cancelled, null, EmptyResults);

        if (HasRunningChild)
        {
            Log.Warn("elevated apply: refused; the previous child is still running");
            return new ElevatedApplyOutcome(ElevatedApplyStatus.Busy, null, EmptyResults);
        }

        var totalPaths = buckets.ToHide.Count + buckets.ToHideWithSystem.Count + buckets.ToShow.Count;
        var files = ElevatedApplyFiles.Create(_tempDirectory);
        try
        {
            // not recorded: a transient request for the elevated child in the OS temp directory,
            // removed on every outcome and never reloaded as managed state.
            await Task.Run(() => File.WriteAllText(files.RequestPath, ElevatedApplyCommand.SerializeRequest(buckets))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("elevated apply: could not write the request file", ex);
            files.TryDelete();
            return new ElevatedApplyOutcome(ElevatedApplyStatus.NotStarted, null, EmptyResults);
        }

        Log.Info("elevated apply: launching", new
        {
            totalPaths,
            hide = buckets.ToHide.Count,
            system = buckets.ToHideWithSystem.Count,
            show = buckets.ToShow.Count,
        });

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = RunChildAsync(
            ElevatedApplyCommand.BuildArguments(files.RequestPath, files.ResultsPath, _storageRoot), started);
        _outstanding = run;

        var status = await WaitAsync(run, started.Task, cancellationToken).ConfigureAwait(false);
        var results = files.ReadResults();

        if (status is ElevatedApplyStatus.Completed or ElevatedApplyStatus.NotStarted)
        {
            files.TryDelete();
            var exitCode = await run.ConfigureAwait(false);
            if (status == ElevatedApplyStatus.Completed)
                Log.Info("elevated apply: exited", new { exitCode, reported = results.Count });
            return new ElevatedApplyOutcome(status, exitCode, results);
        }

        if (status == ElevatedApplyStatus.Cancelled)
            Log.Info("elevated apply: cancelled; the child finishes on its own", new { reported = results.Count, totalPaths });
        else
            Log.Error("elevated apply: child did not exit within the timeout", new
            {
                timeoutSeconds = (int)_childTimeout.TotalSeconds,
                reported = results.Count,
                totalPaths,
            });

        // The child keeps appending to the results file, so the files outlive this call: removed once
        // it exits, by ReleaseAsync at quit, or by the next launch's sweep.
        _outstanding = DeleteAfterExitAsync(run, files);
        return new ElevatedApplyOutcome(status, null, results);
    }

    public async Task ReleaseAsync(TimeSpan bound)
    {
        var outstanding = _outstanding;
        if (outstanding.IsCompleted)
            return;

        if (await Task.WhenAny(outstanding, Task.Delay(bound)).ConfigureAwait(false) != outstanding)
            Log.Warn("elevated apply: a child is still running at quit; the next launch removes its files");
    }

    /// <summary>Launches the child and waits for it to exit. Null exit code: it never ran, or the wait failed.</summary>
    private async Task<int?> RunChildAsync(IReadOnlyList<string> arguments, TaskCompletionSource started)
    {
        Task<int>? child;
        try
        {
            child = await _launch(arguments).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("elevated apply: launch failed", ex);
            return null;
        }

        if (child is null)
            return null;

        started.TrySetResult();
        try
        {
            return await child.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("elevated apply: could not wait for the child", ex);
            return null;
        }
    }

    /// <summary>
    /// Waits for the launch to settle and then for the child to exit, ending early on a cancel, or on
    /// the timeout once the child has started.
    /// </summary>
    private async Task<ElevatedApplyStatus> WaitAsync(Task<int?> run, Task started, CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelled.TrySetResult());

        var first = await Task.WhenAny(run, started, cancelled.Task).ConfigureAwait(false);
        if (first == cancelled.Task)
            return ElevatedApplyStatus.Cancelled;
        if (first == run && !started.IsCompleted)
            return ElevatedApplyStatus.NotStarted;

        using var timer = new CancellationTokenSource();
        var timeout = Task.Delay(_childTimeout, timer.Token);
        first = await Task.WhenAny(run, cancelled.Task, timeout).ConfigureAwait(false);
        timer.Cancel();

        return first == run ? ElevatedApplyStatus.Completed
             : first == cancelled.Task ? ElevatedApplyStatus.Cancelled
             : ElevatedApplyStatus.TimedOut;
    }

    private static async Task DeleteAfterExitAsync(Task<int?> run, ElevatedApplyFiles files)
    {
        await run.ConfigureAwait(false);
        if (!files.TryDelete())
            Log.Warn("elevated apply: could not remove the finished child's files; the next launch retries");
    }
}
