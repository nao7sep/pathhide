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
    /// Inspects the entries a few at a time (<see cref="BoundedVisibility.Width"/>), yielding each
    /// result as it arrives, so one stalled path does not hold back the rows after it.
    /// </summary>
    /// <remarks>
    /// The results are the progress: a separate progress channel could report after the consumer had
    /// finished with the scan.
    /// </remarks>
    public IAsyncEnumerable<ScanResult> ScanAsync(
        IReadOnlyList<PathEntry> entries, CancellationToken cancellationToken = default) =>
        BoundedVisibility.InCompletionOrderAsync(entries, entry => ScanOneAsync(entry, cancellationToken));

    private async Task<ScanResult> ScanOneAsync(PathEntry entry, CancellationToken cancellationToken)
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

        return new ScanResult(entry, inspection, family);
    }
}
