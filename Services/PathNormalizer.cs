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
        // Canonical separator is /; input already uses / since it started with /
        // but could contain mixed separators from a paste
        var result = input.Replace('\\', '/');
        return StripTrailingSeparator(result, '/');
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

        return path.TrimEnd(separator);
    }

    public static bool AreEqual(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
