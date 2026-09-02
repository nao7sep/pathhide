using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using PathHide.Models;

namespace PathHide.Services;

public static class PathNormalizer
{
    public static bool TryNormalize(
        string input,
        [NotNullWhen(true)] out string? normalized,
        out PathFamily family)
    {
        normalized = null;
        family = default;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        // UNC: starts with \\ or //
        if (input.Length >= 3 &&
            (input[0] == '\\' || input[0] == '/') &&
            (input[1] == '\\' || input[1] == '/') &&
            input[2] != '\\' && input[2] != '/')
        {
            family = PathFamily.Unc;
            normalized = NormalizeBackslash(input);
            return true;
        }

        // Windows drive-rooted: letter + colon + separator
        if (input.Length >= 3 &&
            char.IsAsciiLetter(input[0]) &&
            input[1] == ':' &&
            (input[2] == '\\' || input[2] == '/'))
        {
            family = PathFamily.Windows;
            normalized = NormalizeBackslash(input);
            return true;
        }

        // POSIX absolute: starts with /
        if (input[0] == '/')
        {
            family = PathFamily.Posix;
            normalized = NormalizePosix(input);
            return true;
        }

        return false;
    }

    private static string NormalizePosix(string input)
    {
        return StripTrailingSeparator(input, '/');
    }

    /// <summary>Windows and UNC normalize identically: forward slashes to backslashes, then strip
    /// trailing separators. They were two byte-identical methods, which let the two rules drift
    /// apart when only one was edited.</summary>
    private static string NormalizeBackslash(string input) =>
        StripTrailingSeparator(input.Replace('/', '\\'), '\\');

    private static string StripTrailingSeparator(string path, char separator)
    {
        if (path.Length <= 1)
            return path;

        if (path[^1] != separator)
            return path;

        // UNC root like "\\server\share\" — after the leading "\\", count separators.
        // "\\server\share\" inner = "server\share\" has 2 separators → root, strip only trailing.
        // "\\server\" inner = "server\" has 1 separator → incomplete, return as-is to avoid corruption.
        // "\\server\share\foo\" inner = "server\share\foo\" has 3+ separators → normal path, strip.
        if (path.Length >= 4 && path[0] == '\\' && path[1] == '\\')
        {
            var separatorsInInner = path.AsSpan(2).Count('\\');
            if (separatorsInInner <= 1)
                return path;
        }

        var trimmed = path.TrimEnd(separator);

        // Stripping must never leave something that is no longer an absolute path, which is what
        // TryNormalize's [NotNullWhen(true)] contract promises its callers.
        //
        // "//" trimmed to "" was persisted as an entry with an empty path and a row stuck at
        // Error that could only be cleared by removing it. "C://" trimmed to "C:" is worse on
        // Windows: a bare drive is drive-RELATIVE, so a later GetAttributes/SetAttributes resolves
        // it against the process's current directory on that drive — a Hide would set +h on
        // something the user never selected.
        if (trimmed.Length == 0)
            return separator.ToString();
        if (trimmed.Length == 2 && trimmed[1] == ':')
            return trimmed + separator;

        return trimmed;
    }

    public static bool AreEqual(string a, string b)
    {
        var aIsNormalized = TryNormalize(a, out var normalizedA, out var familyA);
        var bIsNormalized = TryNormalize(b, out var normalizedB, out var familyB);

        if (aIsNormalized && bIsNormalized)
        {
            if (familyA != familyB)
                return false;

            // Case-insensitive for every family, including POSIX.
            //
            // This comparison answers "are these the same file?", and its three
            // callers all use it for identity: the add-time duplicate check and
            // the two row/entry reconciliations. macOS is PathHide's only POSIX
            // target and its default APFS volume is case-insensitive, so
            // comparing Ordinal there let two spellings of ONE file become two
            // entries with independent desired states — the rows then contradict
            // each other and whichever applies last silently flips the file,
            // reverting what the user just asked for.
            //
            // A case-SENSITIVE APFS volume is opt-in and rare, and the failure
            // there is the harmless direction: a second genuinely-distinct entry
            // is refused as a duplicate, visibly and without touching anything.
            // The default direction was the destructive one.
            if (familyA == PathFamily.Posix && OperatingSystem.IsMacOS())
            {
                normalizedA = ResolveParentAliases(normalizedA!);
                normalizedB = ResolveParentAliases(normalizedB!);
            }

            return string.Equals(normalizedA, normalizedB, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(a, b, StringComparison.Ordinal);
    }

    private static string ResolveParentAliases(string path)
    {
        var parent = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
            return path;

        // Resolve only the parent. The final component may itself be a symlink,
        // and PathHide deliberately operates on that link rather than its target.
        return MacFs.TryRealPath(parent, out var resolvedParent)
            ? Path.Combine(resolvedParent, name)
            : path;
    }
}
