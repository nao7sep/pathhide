using System;
using System.Threading;
using System.Threading.Tasks;

namespace PathHide.Services;

// Transaction-and-external-effect-conventions: the physical tail remains owned after a wait ends.
internal sealed class BoundedStoreWork
{
    internal static readonly TimeSpan DefaultBound = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _admission = new(1, 1);
    private Task _tail = Task.CompletedTask;

    public async Task<T> RunAsync<T>(Func<T> call, TimeSpan bound, TimeProvider clock,
        CancellationToken cancellationToken = default, Action<Task>? started = null, bool reportAbandonedFailure = true)
    {
        using var deadline = new CancellationTokenSource(bound, clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        var admitted = false;
        Task<T>? work = null;
        try
        {
            await _admission.WaitAsync(stop.Token).ConfigureAwait(false);
            admitted = true;
            // A failed earlier operation still ended physically, so its failure does not poison admission.
            if (!_tail.IsCompleted)
                await _tail.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            work = Task.Run(call);
            _tail = work;
            started?.Invoke(work);
            _ = work.ContinueWith(done =>
            {
                if (done.IsFaulted)
                    _ = done.Exception;
            }, TaskScheduler.Default);
            var result = await work.WaitAsync(stop.Token).ConfigureAwait(false);
            stop.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException)
        {
            if (work is not null)
            {
                _ = work.ContinueWith(done =>
                {
                    if (done.IsFaulted && reportAbandonedFailure)
                        Log.Warn("abandoned storage operation failed", (Exception)done.Exception!);
                }, TaskScheduler.Default);
            }
            if (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                throw new TimeoutException("The storage operation did not finish within its bound.");
            throw;
        }
        finally
        {
            if (admitted)
                _admission.Release();
        }
    }
}
