using System;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Models;

namespace PathHide.Services;

/// <summary>
/// Runs each <see cref="IVisibilityService"/> call off the calling thread and awaits it to its end.
/// Its callers take turns (the scan is stopped before any command, and commands run one at a time),
/// so one path call runs at a time.
/// </summary>
/// <remarks>
/// The calls underneath are blocking stats and flag writes that nothing can interrupt, so a cancel
/// stops before the next call rather than inside one. A path on an unreachable share holds its scan
/// or command until the operating system answers, which is accepted: the user recovers by reconnecting
/// the share, and the bounded exit still lets the app quit.
/// </remarks>
public sealed class VisibilityCalls(IVisibilityService service)
{
    /// <summary>
    /// Inspects <paramref name="path"/>. Like <see cref="IVisibilityService.Inspect"/>, this never throws
    /// for the path's own sake, only <see cref="OperationCanceledException"/> when the caller cancelled
    /// before it started.
    /// </summary>
    public Task<PathInspection> InspectAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => service.Inspect(path), cancellationToken);

    /// <summary>
    /// Resolves the identity of <paramref name="path"/>, or null where it cannot be resolved or the
    /// system resolves none. Throws only <see cref="OperationCanceledException"/>, when the caller
    /// cancelled before it started.
    /// </summary>
    public Task<string?> ResolvePathAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return service.ResolvesPaths ? Task.Run(() => service.ResolvePath(path), cancellationToken) : Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Hides or shows <paramref name="path"/>, doing <paramref name="system"/> to its System attribute,
    /// and returns whether the write turned System on. Throws <see cref="OperationCanceledException"/>
    /// when the caller cancelled before it started, and whatever the write itself threw otherwise.
    /// </summary>
    public Task<bool> WriteAsync(string path, DesiredVisibility desired, SystemBit system, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            if (desired == DesiredVisibility.Hidden)
                return service.Hide(path, system);
            service.Show(path, system);
            return false;
        }, cancellationToken);
}
