using System;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Models;

namespace PathHide.Services;

/// <summary>
/// The one owner of every wait on the file system: each <see cref="IVisibilityService"/> call runs
/// off the calling thread and is waited on for at most <see cref="Timeout"/>, and for no longer than
/// the caller's token allows.
/// </summary>
/// <remarks>
/// <para>The calls underneath are blocking stats and flag writes that nothing can interrupt. On an
/// unreachable UNC server, a stale SMB mount or a half-ejected volume one of them blocks for the
/// operating system's whole network timeout, and a caller that awaited it held the mutation gate for
/// that long: Hide, Show, Add and Reapply All sat dead behind one path, with no way to cancel. So a
/// wait that runs out is abandoned, never awaited: the call finishes on its own thread, its result is
/// discarded and its failure logged.</para>
/// <para>A path whose abandoned call is still running is not touched again until that call returns:
/// a new call first waits for it, within the same bound, and reports the path unresponsive without
/// starting another call if it does not return. That keeps a later Show from racing an earlier Hide
/// still stuck on the same path (and landing before it), keeps repeated commands from piling blocked
/// threads onto one dead share, and still gives a healthy path whose call was abandoned only by a
/// cancel its real answer.</para>
/// </remarks>
public sealed class BoundedVisibility
{
    /// <summary>How long one call may take before its path is reported unresponsive.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly IVisibilityService _service;

    // Physical ownership survives removal/replacement of a row. Windows/UNC spelling is normalized;
    // caller-observed native identities also join known Mac aliases without rewriting stored paths.
    private readonly ConcurrentDictionary<string, Task> _stuck = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _identities = new(StringComparer.Ordinal);

    public BoundedVisibility(IVisibilityService service, TimeSpan? timeout = null)
    {
        _service = service;
        Timeout = timeout ?? DefaultTimeout;
    }

    public TimeSpan Timeout { get; }

    /// <summary>
    /// How many paths are worked on at once, so independent paths do not queue behind one that stalls:
    /// several unresponsive paths cost about one bound together, not one each in turn. Each call holds
    /// a thread of its own while it runs, so the width follows the processors that serve them.
    /// </summary>
    public static int Width { get; } = Math.Max(2, Environment.ProcessorCount);

    /// <summary>
    /// Starts <paramref name="start"/> for each item, at most <see cref="Width"/> at once, and yields
    /// each result as it finishes, in the caller's context, so the caller takes results one at a time.
    /// When the caller stops early or a result throws, the work already started is waited for before
    /// the enumeration ends.
    /// </summary>
    public static async IAsyncEnumerable<TResult> InCompletionOrderAsync<TItem, TResult>(
        IEnumerable<TItem> items, Func<TItem, Task<TResult>> start)
    {
        var running = new List<Task<TResult>>(Width);
        using var next = items.GetEnumerator();
        try
        {
            while (true)
            {
                while (running.Count < Width && next.MoveNext())
                    running.Add(start(next.Current));
                if (running.Count == 0)
                    yield break;

                var finished = await Task.WhenAny(running);
                running.Remove(finished);
                yield return await finished;
            }
        }
        finally
        {
            foreach (var task in running)
                await ((Task)task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Inspects <paramref name="path"/>. A path that does not answer in time is reported as
    /// <see cref="ActualState.Unresponsive"/>; like <see cref="IVisibilityService.Inspect"/>, this
    /// never throws for the path's own sake, only <see cref="OperationCanceledException"/> when the
    /// caller cancels.
    /// </summary>
    public async Task<PathInspection> InspectAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(path, "inspect", () => _service.Inspect(path), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new PathInspection(ActualState.Unresponsive, ItemKind.Unknown);
        }
    }

    /// <summary>
    /// Resolves the identity of <paramref name="path"/>. An entry that does not answer in time
    /// has no resolution, like one that cannot be resolved: null. Throws only
    /// <see cref="OperationCanceledException"/>, when the caller cancels.
    /// </summary>
    public async Task<string?> ResolvePathAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await RunAsync(path, "resolve", () => _service.ResolvePath(path), cancellationToken).ConfigureAwait(false);
            // Only an observed result establishes identity. An abandoned resolver cannot publish late.
            if (resolved is not null)
                _identities[LiteralIdentity(path)] = LiteralIdentity(resolved);
            return resolved;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Hides <paramref name="path"/> in <paramref name="mode"/> or shows it. Throws
    /// <see cref="TimeoutException"/> when the path does not answer in time,
    /// <see cref="OperationCanceledException"/> when the caller cancels, and whatever the write itself
    /// threw otherwise.
    /// </summary>
    public Task WriteAsync(string path, DesiredVisibility desired, WindowsHideMode mode, CancellationToken cancellationToken) =>
        RunAsync(path, desired == DesiredVisibility.Hidden ? "hide" : "show", () =>
        {
            if (desired == DesiredVisibility.Hidden)
                _service.Hide(path, mode);
            else
                _service.Show(path);
            return true;
        }, cancellationToken);

    private async Task<T> RunAsync<T>(string path, string operation, Func<T> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var literal = LiteralIdentity(path);
        var identity = _identities.TryGetValue(literal, out var known) ? known : literal;

        // One deadline for the whole call, the wait for an earlier stuck call included.
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(Timeout);

        if (_stuck.TryGetValue(identity, out var earlier) && !await EndsWithinAsync(earlier, bound.Token))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log.Warn("path not called; an earlier call has not returned", new { path, operation });
            throw new TimeoutException($"An earlier call on this path has not returned: {path}");
        }

        // Deliberately NOT given the token: it would only prevent a start, and a call that never
        // begins is indistinguishable here from one that never returns. A thread of its own, not the
        // shared pool: a call stuck for the network timeout would hold a pool thread the app's own
        // continuations, saves and reads need, and enough of them would leave that work waiting on
        // the pool's slow thread injection.
        var started = Stopwatch.GetTimestamp();
        var work = Task.Factory.StartNew(call, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (!await EndsWithinAsync(work, bound.Token))
        {
            Abandon(path, operation, identity, work, started, timedOut: !cancellationToken.IsCancellationRequested);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"No answer within {Timeout.TotalSeconds:0} s: {path}");
        }

        // A cancel that raced the call's return still wins: the caller asked to stop, so it gets
        // no result to act on, whichever of the two the wait happened to see first.
        cancellationToken.ThrowIfCancellationRequested();
        return await work.ConfigureAwait(false);
    }

    private static string LiteralIdentity(string path) =>
        PathNormalizer.TryNormalize(path, out var normalized, out var family)
            ? PathNormalizer.IdentityKey(normalized, family) : path;

    /// <summary>Whether <paramref name="task"/> ends before <paramref name="bound"/> fires.</summary>
    private static async Task<bool> EndsWithinAsync(Task task, CancellationToken bound)
    {
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (bound.Register(() => stop.TrySetResult()))
            return await Task.WhenAny(task, stop.Task).ConfigureAwait(false) == task;
    }

    /// <summary>
    /// Marks <paramref name="path"/> stuck until <paramref name="work"/> returns, and keeps the
    /// abandoned call's failure from surfacing as an unobserved exception.
    /// </summary>
    /// <remarks>
    /// A wait that timed out is recorded with the call's outcome, and so is any write, whose row shows
    /// Unknown because it may have landed. A read the caller stopped waiting for is recorded only if
    /// it failed: every command stops the background scan, and its healthy reads return at once.
    /// </remarks>
    private void Abandon(string path, string operation, string identity, Task work, long started, bool timedOut)
    {
        var write = operation is "hide" or "show";
        if (timedOut)
            Log.Warn("path did not answer; abandoned the wait", new { path, operation, waitedSeconds = Seconds(started) });
        _stuck[identity] = work;
        _ = work.ContinueWith(
            finished =>
            {
                _stuck.TryRemove(new(identity, finished));
                if (finished.IsFaulted)
                    Log.Warn("abandoned path call failed", (Exception)finished.Exception!,
                        new { path, operation, timedOut, seconds = Seconds(started) });
                else if (timedOut || write)
                    Log.Info("abandoned path call returned", new { path, operation, timedOut, seconds = Seconds(started) });
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private static double Seconds(long started) => Math.Round(Stopwatch.GetElapsedTime(started).TotalSeconds, 1);
}
