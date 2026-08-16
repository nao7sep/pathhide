using System.Collections.Generic;

namespace PathHide.Storage;

/// <summary>
/// Quarantines performed before any window existed (the stores load at startup),
/// journaled so the app edge can report them once a surface exists — an
/// unreported quarantine is a silent reset with extra steps (storage-path
/// conventions: both branches report).
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
