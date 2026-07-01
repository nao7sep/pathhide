namespace PathHide.Backup;

/// <summary>
/// Pure mapping from a home-root file's on-disk relative path to its entry path within the archive. The
/// backup is HOME-ROOT-ONLY (PathHide tracks no external managed roots): each file under
/// <c>~/.pathhide/</c> is mirrored at its path relative to the root, so <c>config.json</c> becomes the
/// entry <c>config.json</c> and <c>paths.json</c> becomes <c>paths.json</c>. All entry paths use forward
/// slashes (see the data-backup conventions).
/// </summary>
public static class BackupArchivePaths
{
    /// <summary>Normalizes a filesystem-relative path to a forward-slash archive path.</summary>
    public static string Normalize(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');

    /// <summary>A file that lives under <c>~/.pathhide/</c>: its relative path is the archive path.</summary>
    public static string ForHomeFile(string relativePath) => Normalize(relativePath);
}
