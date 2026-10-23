using System;
using System.Collections.Concurrent;
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
    /// Inspects <paramref name="path"/>. A path that does not answer in time is reported as
    /// <see cref="ActualState.Unresponsive"/>; like <see cref="IVisibilityService.Inspect"/>, this
    /// never throws for the path's own sake, only <see cref="OperationCanceledException"/> when the
    /// caller cancels.
    /// </summary>
    public async Task<PathInspection> InspectAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(path, () => _service.Inspect(path), cancellationToken).ConfigureAwait(false);
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
            var resolved = await RunAsync(path, () => _service.ResolvePath(path), cancellationToken).ConfigureAwait(false);
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
    /// Hides or shows <paramref name="path"/>, hiding in <paramref name="mode"/>. Throws <see cref="TimeoutException"/> when the path does
    /// not answer in time, <see cref="OperationCanceledException"/> when the caller cancels, and
    /// whatever the write itself threw otherwise.
    /// </summary>
    public Task WriteAsync(string path, DesiredVisibility desired, WindowsHideMode mode, CancellationToken cancellationToken) =>
        RunAsync(path, () =>
        {
            if (desired == DesiredVisibility.Hidden)
                _service.Hide(path, mode);
            else
                _service.Show(path);
            return true;
        }, cancellationToken);

    private async Task<T> RunAsync<T>(string path, Func<T> call, CancellationToken cancellationToken)
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
            throw new TimeoutException($"An earlier call on this path has not returned: {path}");
        }

        // Deliberately NOT given the token: it would only prevent a start, and a call that never
        // begins is indistinguishable here from one that never returns.
        var work = Task.Run(call, CancellationToken.None);
        if (!await EndsWithinAsync(work, bound.Token))
        {
            Abandon(path, identity, work);
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
    private void Abandon(string path, string identity, Task work)
    {
        Log.Warn("path did not answer; abandoned the wait", new { path });
        _stuck[identity] = work;
        _ = work.ContinueWith(
            finished =>
            {
                _stuck.TryRemove(new(identity, finished));
                if (finished.IsFaulted)
                    Log.Warn("abandoned path call failed", (Exception)finished.Exception!, new { path });
                else
                    Log.Info("abandoned path call returned", new { path });
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }
}
