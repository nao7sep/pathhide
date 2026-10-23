using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PathHide.Services;

/// <summary>How one elevated apply ended, as far as the caller is concerned.</summary>
public enum ElevatedApplyStatus
{
    /// <summary>The child ran and exited; <see cref="ElevatedApplyOutcome.Results"/> contains its available report.</summary>
    Completed,

    /// <summary>No child ran: preparation or launch failed.</summary>
    NotStarted,

    /// <summary>No child ran: the user declined the administrator approval prompt.</summary>
    Declined,

    /// <summary>The user cancelled the wait. The child, if it started, finishes on its own.</summary>
    Cancelled,

    /// <summary>The child did not exit in time. It keeps running; unreported paths may still change.</summary>
    TimedOut,

    /// <summary>Refused: an earlier apply still owns preparation, child execution or cleanup.</summary>
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
    IReadOnlyDictionary<string, PathApplyResult> Results);

/// <summary>Runs access-denied paths through one elevated child process.</summary>
public interface IElevatedApplicator
{
    /// <summary>Whether an earlier elevated operation still owns preparation, a child or file cleanup.</summary>
    bool HasRunningChild { get; }

    /// <summary>
    /// Applies <paramref name="buckets"/> through an elevated child, or refuses with
    /// <see cref="ElevatedApplyStatus.Busy"/> while an earlier elevated operation still owns work. Returns once; nothing
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

/// <summary>Thrown by an <see cref="ElevatedChildLauncher"/> when the user declines the approval prompt.</summary>
public sealed class ElevationDeclinedException() : Exception("The administrator approval prompt was declined.");

/// <summary>
/// The platform-neutral half of the elevated retry: writes the request, launches the child through an
/// <see cref="ElevatedChildLauncher"/>, bounds foreground file/child waits, reads the results, and owns the
/// temp files and the one child that may outlive a call.
/// </summary>
/// <remarks>
/// <para>An elevated child cannot be stopped from here — a higher-integrity process is not ours to signal —
/// so a cancel or a timeout only ends the wait. At most one child runs: while one is outstanding, a new
/// apply is refused instead of launched, because two children writing the same paths let the older
/// one's late write undo the newer one (hide a path the user has just shown) behind the list's back.</para>
/// <para>Across a GUI restart, the shared Windows writer reserves each submitted path through the
/// actual attribute write. The child checks its original GUI process after acquiring that reservation:
/// it finishes an already admitted write before the restarted GUI can enter, and refuses later writes
/// once its GUI has exited. This needs no persistent job state or change to the consent flow.</para>
/// <para>The first worker await always yields to the thread pool, even if it already completed;
/// subsequent awaits do not capture the caller's context. Reading results and removing temp files
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

    private static readonly IReadOnlyDictionary<string, PathApplyResult> EmptyResults =
        new Dictionary<string, PathApplyResult>(StringComparer.Ordinal);

    private readonly ElevatedChildLauncher _launch;
    private readonly string _tempDirectory;
    private readonly string _storageRoot;
    private readonly TimeSpan _childTimeout;

    internal bool BindWindowsTargets { get; init; } = OperatingSystem.IsWindows();
    internal TimeSpan FileWaitBound { get; init; } = BoundedStoreWork.DefaultBound;
    internal Action<string, byte[]> WriteRequest { get; init; } = File.WriteAllBytes;
    internal Func<ElevatedApplyFiles, IReadOnlyDictionary<string, PathApplyResult>> ReadResults { get; init; } = files => files.ReadResults();
    internal Func<ElevatedApplyFiles, bool> DeleteFiles { get; init; } = files => files.TryDelete();

    // The preparation-to-cleanup lifetime of the newest elevated operation. Completed when none
    // is outstanding. ApplyAsync replaces it; HasRunningChild and ReleaseAsync observe it during quit.
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

        var totalPaths = buckets.Count;
        string requestDigest = string.Empty;
        var targets = new List<WindowsAttributeTarget>();
        var files = ElevatedApplyFiles.Create(_tempDirectory);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _outstanding = settled.Task; // Claim preparation too, before the first wait.
        var fileWork = new BoundedStoreWork();
        Task fileTail = Task.CompletedTask;
        Task<int?>? run = null;
        var status = ElevatedApplyStatus.NotStarted;
        try
        {
            try
            {
                // not recorded: transient IPC, never reloaded as managed state.
                await fileWork.RunAsync(() =>
                {
                    if (BindWindowsTargets)
                    {
                        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var path in buckets.ToHide.Concat(buckets.ToHideWithSystem)
                            .Concat(buckets.ToHideClearingSystem).Concat(buckets.ToShow).Concat(buckets.ToShowClearingSystem))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                var target = new WindowsAttributeTarget(path, writable: false);
                                targets.Add(target); // Held until child and physical preparation settle.
                                identities[path] = target.Identity;
                            }
                            catch (Exception ex)
                            {
                                // The child rejects this target without mutation; independent approved targets remain usable.
                                Log.Warn("elevated apply: target identity unavailable", ex, new { path });
                            }
                        }
                        buckets = buckets with { Identities = identities };
                    }
                    var request = System.Text.Encoding.UTF8.GetBytes(ElevatedApplyCommand.SerializeRequest(buckets));
                    requestDigest = ElevatedApplyCommand.RequestDigest(request);
                    WriteRequest(files.RequestPath, request);
                    return true;
                }, FileWaitBound, TimeProvider.System, cancellationToken, started: work => fileTail = work)
                    .ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                status = ElevatedApplyStatus.Cancelled;
                return new ElevatedApplyOutcome(status, null, EmptyResults);
            }
            catch (Exception ex)
            {
                Log.Error("elevated apply: could not write the request file", ex);
                return new ElevatedApplyOutcome(status, null, EmptyResults);
            }

            // A cancelled preparation must never raise consent later, even if its write just landed.
            if (cancellationToken.IsCancellationRequested)
            {
                status = ElevatedApplyStatus.Cancelled;
                return new ElevatedApplyOutcome(status, null, EmptyResults);
            }

            Log.Info("elevated apply: launching", new
            {
                totalPaths,
                hide = buckets.ToHide.Count,
                system = buckets.ToHideWithSystem.Count,
                hideClearingSystem = buckets.ToHideClearingSystem.Count,
                show = buckets.ToShow.Count,
                showClearingSystem = buckets.ToShowClearingSystem.Count,
            });
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var declined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run = RunChildAsync(files, requestDigest, started, declined);
            status = await WaitAsync(run, started.Task, declined.Task, cancellationToken).ConfigureAwait(false);

            // Cancelled while the approval prompt is still open: the prompt cannot be taken back, so the
            // request is. A child approved later would otherwise apply an older change than the list
            // may by then ask for.
            if (status == ElevatedApplyStatus.Cancelled && !started.Task.IsCompleted && !files.TryWithdrawRequest())
                Log.Warn("elevated apply: could not withdraw the request; a child approved later applies it");

            IReadOnlyDictionary<string, PathApplyResult> results;
            try
            {
                // Cancel still collects already-flushed verdicts, within a separate file-read bound.
                results = await fileWork.RunAsync(() => ReadResults(files), FileWaitBound, TimeProvider.System,
                    started: work => fileTail = work).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("elevated apply: results unavailable within the file wait", ex);
                results = EmptyResults;
            }

            if (status is ElevatedApplyStatus.Completed or ElevatedApplyStatus.NotStarted or ElevatedApplyStatus.Declined)
            {
                var exitCode = await run.ConfigureAwait(false);
                // 1 means some paths failed, 2 a refused launch, 3 a child that failed or could not report;
                // its own records may not have reached this side, so the exit itself is the warning.
                if (status == ElevatedApplyStatus.Completed && exitCode == 0)
                    Log.Info("elevated apply: exited", new { exitCode, reported = results.Count });
                else if (status == ElevatedApplyStatus.Completed)
                    Log.Warn("elevated apply: exited", new { exitCode, reported = results.Count });
                return new ElevatedApplyOutcome(status, exitCode, results);
            }

            if (status == ElevatedApplyStatus.Cancelled && !started.Task.IsCompleted)
                Log.Info("elevated apply: cancelled before the child started", new { totalPaths });
            else if (status == ElevatedApplyStatus.Cancelled)
                Log.Info("elevated apply: cancelled; the child finishes on its own", new { reported = results.Count, totalPaths });
            else
                Log.Error("elevated apply: child did not exit within the timeout", new
                {
                    timeoutSeconds = (int)_childTimeout.TotalSeconds,
                    reported = results.Count,
                    totalPaths,
                });
            return new ElevatedApplyOutcome(status, null, results);
        }
        finally
        {
            // Physical preparation/read, child exit and cleanup remain one lifetime. A timed-out
            // file operation is joined before deleting its files; no late evidence reaches the UI.
            var cleanup = DeleteAfterSettlementAsync(run, fileTail, files, settled, targets);
            if ((status is ElevatedApplyStatus.Completed or ElevatedApplyStatus.NotStarted or ElevatedApplyStatus.Declined) && fileTail.IsCompleted)
            {
                try { await cleanup.WaitAsync(FileWaitBound).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    Log.Warn("elevated apply: cleanup still running; the outcome is already known");
                }
            }
        }
    }

    public async Task ReleaseAsync(TimeSpan bound)
    {
        var outstanding = _outstanding;
        if (outstanding.IsCompleted)
            return;

        if (await Task.WhenAny(outstanding, Task.Delay(bound)).ConfigureAwait(false) != outstanding)
            Log.Warn("elevated apply: a child is still running at quit; the next launch removes its files");
    }

    /// <summary>
    /// Launches the child and waits for it to exit. Null exit code: it never ran, or the wait failed;
    /// <paramref name="declined"/> completes when it never ran because the user declined the prompt.
    /// </summary>
    private async Task<int?> RunChildAsync(ElevatedApplyFiles files, string requestDigest, TaskCompletionSource started,
        TaskCompletionSource declined)
    {
        Task<int>? child;
        try
        {
            child = await _launch(ElevatedApplyCommand.BuildArguments(files.RequestPath, files.ResultsPath,
                _storageRoot, ElevatedApplyCommand.Parent.Current, requestDigest, Log.Session)).ConfigureAwait(false);
        }
        catch (ElevationDeclinedException)
        {
            Log.Info("elevated apply: administrator approval declined");
            declined.TrySetResult();
            return null;
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
    private async Task<ElevatedApplyStatus> WaitAsync(Task<int?> run, Task started, Task declined,
        CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => cancelled.TrySetResult());

        var first = await Task.WhenAny(run, started, cancelled.Task).ConfigureAwait(false);
        if (first == cancelled.Task)
            return ElevatedApplyStatus.Cancelled;
        if (first == run && !started.IsCompleted)
            return declined.IsCompleted ? ElevatedApplyStatus.Declined : ElevatedApplyStatus.NotStarted;

        using var timer = new CancellationTokenSource();
        var timeout = Task.Delay(_childTimeout, timer.Token);
        first = await Task.WhenAny(run, cancelled.Task, timeout).ConfigureAwait(false);
        timer.Cancel();

        return first == run ? ElevatedApplyStatus.Completed
             : first == cancelled.Task ? ElevatedApplyStatus.Cancelled
             : ElevatedApplyStatus.TimedOut;
    }

    private async Task DeleteAfterSettlementAsync(Task<int?>? run, Task fileTail,
        ElevatedApplyFiles files, TaskCompletionSource settled, List<WindowsAttributeTarget> targets)
    {
        try
        {
            if (run is not null)
                await run.ConfigureAwait(false);
            // Observe a failed physical operation too; cleanup starts only after it really ends.
            await fileTail.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            if (!await Task.Run(() => DeleteFiles(files)).ConfigureAwait(false))
                Log.Warn("elevated apply: could not remove the finished operation's files; the next launch retries");
        }
        catch (Exception ex)
        {
            Log.Warn("elevated apply: cleanup failed; the next launch retries", ex);
        }
        finally
        {
            foreach (var target in targets) target.Dispose();
            settled.TrySetResult();
        }
    }
}
