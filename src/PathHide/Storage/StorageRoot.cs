using System;
using System.IO;
using System.Text.RegularExpressions;

namespace PathHide.Storage;

/// <summary>
/// The single storage root for the app's own files, under <c>~/.pathhide/</c>. The root is
/// <c>PATHHIDE_HOME</c> when that environment variable is set and non-empty (its value is expanded for a
/// leading <c>~</c> and for environment references, then made absolute against the home directory),
/// otherwise the default <c>~/.pathhide/</c>. Every subpath is derived from whichever root won, so the
/// one variable relocates the whole tree. The working directory is never a base for any path, per the
/// storage-path conventions. <c>PATHHIDE_HOME</c> is the one relocation seam, used the same way by tests
/// and in production.
/// </summary>
public static class StorageRoot
{
    /// <summary>Environment variable that relocates the entire storage root.</summary>
    public const string HomeEnvironmentVariable = "PATHHIDE_HOME";

    private static readonly object Gate = new();

    // The resolved root is cached alongside the raw override value it was computed from, so production
    // resolves once while a test that changes PATHHIDE_HOME (to a throwaway directory) re-resolves.
    private static string? _cachedOverride;
    private static bool _cachedOverridePresent;
    private static string? _cachedRoot;

    public static string Directory
    {
        get
        {
            lock (Gate)
            {
                var rawOverride = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
                var hasOverride = !string.IsNullOrEmpty(rawOverride?.Trim());

                if (_cachedRoot is null ||
                    _cachedOverridePresent != hasOverride ||
                    !string.Equals(_cachedOverride, rawOverride, StringComparison.Ordinal))
                {
                    _cachedRoot = Resolve(rawOverride, hasOverride);
                    _cachedOverride = rawOverride;
                    _cachedOverridePresent = hasOverride;
                }

                return _cachedRoot;
            }
        }
    }

    public static string LogsDirectory => Path.Combine(Directory, "logs");

    public static void EnsureExists()
    {
        System.IO.Directory.CreateDirectory(Directory);
    }

    private static string Resolve(string? rawOverride, bool hasOverride)
    {
        var home = HomeDirectory();

        if (hasOverride)
        {
            return ResolveOverride(rawOverride!.Trim(), home);
        }

        return Path.Combine(home, ".pathhide");
    }

    // Matches `${VAR}`, `$VAR` (POSIX) and `%VAR%` (Windows) environment references.
    private static readonly Regex EnvReferencePattern = new(
        @"\$\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)\}|\$(?<bare>[A-Za-z_][A-Za-z0-9_]*)|%(?<win>[A-Za-z_][A-Za-z0-9_]*)%",
        RegexOptions.Compiled);

    /// <summary>
    /// Expands <c>${VAR}</c> / <c>$VAR</c> / <c>%VAR%</c> references against the environment. An unset
    /// reference expands to empty — matching shell behavior and the TypeScript/Rust resolvers in the
    /// fleet — rather than being left as a literal that would become a directory name.
    /// </summary>
    private static string ExpandEnvReferences(string value) =>
        EnvReferencePattern.Replace(value, match =>
        {
            var name = match.Groups["braced"].Success ? match.Groups["braced"].Value
                : match.Groups["bare"].Success ? match.Groups["bare"].Value
                : match.Groups["win"].Value;
            return Environment.GetEnvironmentVariable(name) ?? string.Empty;
        });

    /// <summary>
    /// Expands environment references and a leading <c>~</c> in the override value, then makes it
    /// absolute against the home directory (never the working directory) so the override can never
    /// reintroduce a cwd dependence. An override that is set but expands to nothing (an unset
    /// <c>$VAR</c>/<c>%VAR%</c>) is a reported startup error, not a silent collapse onto the home directory.
    /// </summary>
    private static string ResolveOverride(string value, string home)
    {
        value = ExpandEnvReferences(value).Trim();

        if (value.Length == 0)
        {
            throw new InvalidOperationException(
                HomeEnvironmentVariable + " is set but expands to an empty path (an unset $VAR/%VAR%?). " +
                "Set it to a usable directory, or unset it to use the default.");
        }

        if (value == "~")
        {
            value = home;
        }
        else if (value.StartsWith("~/", StringComparison.Ordinal) ||
                 value.StartsWith("~" + Path.DirectorySeparatorChar))
        {
            value = Path.Combine(home, value[2..]);
        }

        // A relative override is resolved against the home directory, not the working directory.
        return Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(home, value));
    }

    private static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("HOME");
        }

        if (string.IsNullOrEmpty(home))
        {
            // The convention forbids the working directory as any base; with no home directory there is
            // no usable storage root, so fail loudly rather than silently writing under the cwd.
            throw new InvalidOperationException(
                "Cannot resolve a storage root: the user's home directory is unknown. " +
                "Set the home directory or " + HomeEnvironmentVariable + " to an absolute path.");
        }

        return home;
    }
}
