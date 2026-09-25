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
    private readonly BoundedVisibility _visibility;

    public PathScanner(BoundedVisibility visibility)
    {
        _visibility = visibility;
    }

    /// <summary>
    /// Inspects each entry in order, yielding one result per entry.
    /// </summary>
    /// <remarks>
    /// There is no progress callback: the results ARE the progress. A separate
    /// <c>IProgress&lt;int&gt;</c> reported the same count through a second, asynchronous channel,
    /// so a report could land after the consumer had already finished with the scan — which is
    /// what forced the consumer to check whether each report was still current.
    /// </remarks>
    public async IAsyncEnumerable<ScanResult> ScanAsync(
        IReadOnlyList<PathEntry> entries,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                // Bounded, and abandoned on cancel: a stalled share reports the path unresponsive
                // rather than holding the scan (and every command that pauses it) on one stat.
                inspection = await _visibility.InspectAsync(entry.Path, cancellationToken);
            }

            // Per-item, scales with the path list — debug only.
            Log.Debug("scanned", new
            {
                path = entry.Path,
                actualState = inspection.ActualState,
                itemKind = inspection.ItemKind,
                family,
            });

            yield return new ScanResult(entry, inspection, family);
        }
    }
}
