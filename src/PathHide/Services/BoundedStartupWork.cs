using System;
using System.Threading;
using System.Threading.Tasks;

namespace PathHide.Services;

// Required startup work has no successor: a timeout opens the startup failure surface.
internal static class BoundedStartupWork
{
    internal static async Task<T> RunAsync<T>(Func<T> prepare, Action<T>? abandoned = null,
        TimeSpan? bound = null, CancellationToken cancellationToken = default)
    {
        Task<T>? physical = null;
        try
        {
            return await new BoundedStoreWork().RunAsync(prepare, bound ?? BoundedStoreWork.DefaultBound,
                TimeProvider.System, cancellationToken, started: task => physical = (Task<T>)task,
                reportAbandonedFailure: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (physical is not null)
            {
                _ = physical.ContinueWith(done =>
                {
                    if (done.IsFaulted)
                    {
                        _ = Log.ReportStartup("startup: abandoned preparation failed", done.Exception!, warning: true);
                        return;
                    }
                    if (done.Status != TaskStatus.RanToCompletion || abandoned is null)
                        return;
                    try { abandoned(done.Result); }
                    catch (Exception cleanup)
                    {
                        _ = Log.ReportStartup("startup: abandoned result cleanup failed", cleanup, warning: true);
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
            throw;
        }
    }
}
