using System.Collections.Generic;

namespace PathHide.Storage;

/// <summary>
/// Quarantines performed before the window exists, held for its recovery notice.
/// </summary>
public static class QuarantineJournal
{
    private static readonly List<string> Paths = [];

    public static void Record(string quarantinePath) => Paths.Add(quarantinePath);

    public static IReadOnlyList<string> Drain()
    {
        var drained = Paths.ToArray();
        Paths.Clear();
        return drained;
    }
}
