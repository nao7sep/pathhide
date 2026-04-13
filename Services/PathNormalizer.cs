using System;
using System.Diagnostics.CodeAnalysis;
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
            normalized = NormalizeUnc(input);
            return true;
        }

        // Windows drive-rooted: letter + colon + separator
        if (input.Length >= 3 &&
            char.IsAsciiLetter(input[0]) &&
            input[1] == ':' &&
            (input[2] == '\\' || input[2] == '/'))
        {
            family = PathFamily.Windows;
            normalized = NormalizeWindows(input);
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

    private static string NormalizeWindows(string input)
    {
        var result = input.Replace('/', '\\');
        return StripTrailingSeparator(result, '\\');
    }

    private static string NormalizeUnc(string input)
    {
        var result = input.Replace('/', '\\');
        return StripTrailingSeparator(result, '\\');
    }

    private static string StripTrailingSeparator(string path, char separator)
    {
        if (path.Length <= 1)
            return path;

        // Don't strip if the path is a root:
        //   POSIX: "/"
        //   Windows: "C:\"
        //   UNC: "\\server\share" — keep at least \\x\y
        if (path[^1] != separator)
            return path;

        // POSIX root
        if (path.Length == 1)
            return path;

        // Windows root like "C:\"
        if (path.Length == 3 && path[1] == ':')
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

        return path.TrimEnd(separator);
    }

    public static bool AreEqual(string a, string b)
    {
        var aIsNormalized = TryNormalize(a, out var normalizedA, out var familyA);
        var bIsNormalized = TryNormalize(b, out var normalizedB, out var familyB);

        if (aIsNormalized && bIsNormalized)
        {
            if (familyA != familyB)
                return false;

            return string.Equals(
                normalizedA,
                normalizedB,
                familyA == PathFamily.Posix ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(a, b, StringComparison.Ordinal);
    }
}
