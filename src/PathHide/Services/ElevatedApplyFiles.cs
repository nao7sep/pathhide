using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PathHide.Services;

/// <summary>
/// The two temp files one elevated apply uses: the request (the path lists the parent writes) and the
/// results (one line per path the child writes back). Both are plain-text inventories of the paths the
/// user is hiding, so every outcome removes them, and a launch removes any an earlier run had to leave.
/// </summary>
/// <remarks>
/// Each name carries this host and this process id, so a later sweep can prove a file's owner is
/// gone before deleting it (managed-runtime-dependencies conventions, staging): a file whose
/// recorded process still runs may belong to a live PathHide that has not read its results yet. The
/// elevated child holds the results file open without delete sharing for its whole run, so deleting
/// it while that child still runs fails and the file waits for the next sweep.
/// </remarks>
public sealed record ElevatedApplyFiles(string RequestPath, string ResultsPath)
{
    private const string Prefix = "pathhide-apply-";
    private const string RequestSuffix = ".request.json";
    private const string ResultsSuffix = ".results.jsonl";

    private static readonly IReadOnlyDictionary<string, bool> EmptyResults =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>A fresh, unguessable pair of names in <paramref name="directory"/> owned by this process.</summary>
    public static ElevatedApplyFiles Create(string directory)
    {
        var stem = Path.Combine(directory, $"{Prefix}{Host()}.{Environment.ProcessId}.{NanoId.New()}");
        return new ElevatedApplyFiles(stem + RequestSuffix, stem + ResultsSuffix);
    }

    /// <summary>
    /// The per-path outcomes the child has written so far, keyed by the exact path it was handed.
    /// Read with write sharing, because a child that is still running holds the file open for writing.
    /// </summary>
    public IReadOnlyDictionary<string, bool> ReadResults()
    {
        try
        {
            if (!File.Exists(ResultsPath))
                return EmptyResults;

            string text;
            using (var stream = new FileStream(ResultsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                text = reader.ReadToEnd();

            var byPath = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var result in ElevatedApplyResults.Parse(text))
                byPath[result.Path] = result.Ok;
            return byPath;
        }
        catch (Exception ex)
        {
            Log.Error("elevated apply: failed to read results file", ex, new { path = ResultsPath });
            return EmptyResults;
        }
    }

    /// <summary>Deletes both files. Returns whether neither is left.</summary>
    public bool TryDelete() => TryDelete(RequestPath) & TryDelete(ResultsPath);

    /// <summary>
    /// Deletes the files earlier runs left in <paramref name="directory"/>, but only those this host
    /// wrote from a process that has since exited. A file the child still holds open fails to delete
    /// and is left for the next launch.
    /// </summary>
    /// <param name="isProcessRunning">Whether a process id is live; the real check by default.</param>
    /// <returns>How many files were deleted.</returns>
    public static int SweepLeftovers(string directory, Func<int, bool>? isProcessRunning = null)
    {
        isProcessRunning ??= IsProcessRunning;
        var deleted = 0;
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(directory, Prefix + "*");
        }
        catch (Exception ex)
        {
            Log.Warn("elevated apply: could not list leftover files", ex);
            return 0;
        }

        foreach (var path in candidates)
        {
            if (!TryReadOwner(Path.GetFileName(path), out var host, out var processId)
                || !string.Equals(host, Host(), StringComparison.OrdinalIgnoreCase)
                || isProcessRunning(processId))
            {
                continue;
            }

            if (TryDelete(path))
                deleted++;
        }

        if (deleted > 0)
            Log.Info("elevated apply: removed files an earlier run left", new { deleted });
        return deleted;
    }

    /// <summary>Reads the owner a file name records, or false when it is not one of these names.</summary>
    internal static bool TryReadOwner(string fileName, out string host, out int processId)
    {
        host = string.Empty;
        processId = 0;
        if (!fileName.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        string stem;
        if (fileName.EndsWith(RequestSuffix, StringComparison.Ordinal))
            stem = fileName[Prefix.Length..^RequestSuffix.Length];
        else if (fileName.EndsWith(ResultsSuffix, StringComparison.Ordinal))
            stem = fileName[Prefix.Length..^ResultsSuffix.Length];
        else
            return false;

        // host.pid.id — neither the host (sanitized below) nor the id's alphabet contains a dot.
        var parts = stem.Split('.');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[2].Length == 0
            || !int.TryParse(parts[1], out processId) || processId <= 0)
        {
            return false;
        }

        host = parts[0];
        return true;
    }

    /// <summary>This machine's name, reduced to characters that cannot be mistaken for a separator.</summary>
    private static string Host()
    {
        var builder = new StringBuilder();
        foreach (var c in Environment.MachineName)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_');
        return builder.Length > 0 ? builder.ToString() : "host";
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception)
        {
            // Unable to tell (e.g. access to another session's process): treat it as live and keep the file.
            return true;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("elevated apply: could not delete a temp file yet", ex, new { path });
            return false;
        }
    }
}
