using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Models;

namespace PathHide.Services;

public sealed record ScanResult(
    PathEntry Entry,
    PathInspection Inspection,
    PathFamily Family);

public sealed class PathScanner
{
    private readonly IVisibilityService _visibilityService;

    public PathScanner(IVisibilityService visibilityService)
    {
        _visibilityService = visibilityService;
    }

    /// <summary>
    /// Inspects one path off the UI thread, abandoning the wait if the scan is cancelled first.
    /// </summary>
    /// <remarks>
    /// Handing the token to <c>Task.Run</c> only stops the work item from STARTING; once
    /// <c>Inspect</c> is running it cannot be interrupted, and it is a blocking stat. On an
    /// unreachable UNC server or a stale SMB mount under /Volumes that blocks for tens of
    /// seconds per path — and UNC is a first-class path family here — so Cancel did nothing
    /// observable until the current item returned, and every mutating command sat dead behind
    /// it, because each one pauses the scan and then awaits it with no timeout.
    /// <para>Racing the probe against the token lets the scan stop waiting. The abandoned work
    /// finishes on its own thread; its result is discarded and its failure observed, so it
    /// cannot resurface as an unobserved task exception.</para>
    /// </remarks>
    private async Task<PathInspection> InspectOrAbandonAsync(
        string path, CancellationToken cancellationToken)
    {
        // Deliberately NOT given the token: it would only prevent a start, and a probe that
        // never begins is indistinguishable here from one that never returns.
        var probe = Task.Run(() => _visibilityService.Inspect(path), CancellationToken.None);

        var abandoned = new TaskCompletionSource();
        using (cancellationToken.Register(() => abandoned.TrySetResult()))
        {
            if (await Task.WhenAny(probe, abandoned.Task).ConfigureAwait(false) != probe)
            {
                Observe(probe, path);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return await probe.ConfigureAwait(false);
    }

    /// <summary>Keeps an abandoned probe's failure from surfacing as an unobserved exception.</summary>
    private static void Observe(Task probe, string path) =>
        _ = probe.ContinueWith(
            t => Log.Warn("scan: abandoned a blocked inspection", (Exception)t.Exception!, new { path }),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    public async IAsyncEnumerable<ScanResult> ScanAsync(
        IReadOnlyList<PathEntry> entries,
        IProgress<int>? progress = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = entries[i];

            PathInspection inspection;
            PathFamily family;

            if (!PathNormalizer.TryNormalize(entry.Path, out _, out family))
            {
                // Path doesn't parse — treat as error
                inspection = new PathInspection(ActualState.Error, ItemKind.Unknown);
                family = default;
            }
            else
            {
                inspection = await InspectOrAbandonAsync(entry.Path, cancellationToken);
            }

            // Per-item, scales with the path list — debug only.
            Log.Debug("scanned", new
            {
                path = entry.Path,
                actualState = inspection.ActualState,
                itemKind = inspection.ItemKind,
                family,
            });

            progress?.Report(i + 1);
            yield return new ScanResult(entry, inspection, family);
        }
    }
}
