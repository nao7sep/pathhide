using System;

namespace PathHide.Backup;

/// <summary>
/// The optimistic exclude list for the <c>~/.pathhide/</c> home root: everything under the root is
/// backed up except the entries here. Pure so the "did we pick the right files?" decision is
/// unit-testable. Durable data (<c>config.json</c>, <c>paths.json</c>) is captured; only genuinely
/// throwaway, feature-owned, or transient paths are dropped. Paths are the forward-slash relative path
/// under the root; a directory is passed with a trailing <c>/</c> so a subtree can be pruned rather than
/// walked and discarded.
/// </summary>
public static class HomeRootExclusions
{
    /// <summary>
    /// True when a home-root path must not be backed up:
    /// <list type="bullet">
    /// <item><c>backups/</c> — the feature's own archives and index; backing them up would recurse.</item>
    /// <item><c>logs/</c> — per-session logs, recreatable and noisy.</item>
    /// <item><c>*.tmp</c> — atomic-write temporaries (they never outlive a write, but a crash can leave one).</item>
    /// <item><c>*.bak</c> — the retired last-good sidecar; excluded defensively so a stray legacy copy
    /// (or one written by an older build) is never captured.</item>
    /// <item><c>.DS_Store</c>, <c>Thumbs.db</c> — OS-generated directory metadata, never user data.</item>
    /// </list>
    /// The comparisons are case-insensitive so the exclusions hold on case-insensitive filesystems
    /// (macOS default, Windows) regardless of how a path is cased on disk.
    /// </summary>
    public static bool IsExcluded(string relativePath)
    {
        var path = BackupArchivePaths.Normalize(relativePath);

        if (path.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("backups/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = LastSegment(path);
        return string.Equals(name, ".DS_Store", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }
}
