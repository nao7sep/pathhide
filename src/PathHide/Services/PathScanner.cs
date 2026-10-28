using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    private readonly VisibilityCalls _visibility;

    public PathScanner(VisibilityCalls visibility)
    {
        _visibility = visibility;
    }

    /// <summary>
    /// Inspects the entries one after another, yielding each result as it arrives; a cancel stops
    /// before the next entry.
    /// </summary>
    /// <remarks>
    /// The results are the progress: a separate progress channel could report after the consumer had
    /// finished with the scan.
    /// </remarks>
    public async IAsyncEnumerable<ScanResult> ScanAsync(
        IReadOnlyList<PathEntry> entries, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var entry in entries)
            yield return await ScanOneAsync(entry, cancellationToken);
    }

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
