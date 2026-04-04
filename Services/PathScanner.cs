using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PathHide.Models;
using Serilog;

namespace PathHide.Services;

public sealed record ScanResult(
    PathEntry Entry,
    PathInspection Inspection,
    PathFamily Family);

public sealed class PathScanner
{
    private static readonly ILogger Log = Serilog.Log.ForContext<PathScanner>();

    private readonly IVisibilityService _visibilityService;

    public PathScanner(IVisibilityService visibilityService)
    {
        _visibilityService = visibilityService;
    }

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
                // Run the I/O-bound inspection off the UI thread
                inspection = await Task.Run(
                    () => _visibilityService.Inspect(entry.Path),
                    cancellationToken);
            }

            Log.Debug(
                "Scanned {Path}: {ActualState}, {ItemKind}, {Family}",
                entry.Path, inspection.ActualState, inspection.ItemKind, family);

            progress?.Report(i + 1);
            yield return new ScanResult(entry, inspection, family);
        }
    }
}
