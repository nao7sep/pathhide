using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace PathHide.Services;

/// <summary>
/// The result of one elevated apply pass: the child process exit code (a coarse
/// 0 = all ok / 1 = some failed signal, or a negative sentinel when the child never ran)
/// and the authoritative per-path outcomes the child reported. A path absent from
/// <see cref="Results"/> was never reported on — e.g. the user cancelled the UAC prompt,
/// or the results file could not be read — and the caller decides what that means.
/// </summary>
public sealed record ElevatedApplyOutcome(int ExitCode, IReadOnlyDictionary<string, bool> Results);

[SupportedOSPlatform("windows")]
public static class WindowsElevatedApplicator
{
    private static readonly IReadOnlyDictionary<string, bool> EmptyResults =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    public static async Task<ElevatedApplyOutcome> ApplyAsync(
        IEnumerable<string> toHide,
        IEnumerable<string> toHideWithSystem,
        IEnumerable<string> toShow)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Error("elevated apply: no process path");
            return new ElevatedApplyOutcome(-1, EmptyResults);
        }

        var hideList   = toHide.ToList();
        var systemList = toHideWithSystem.ToList();
        var showList   = toShow.ToList();

        // The runas verb forces UseShellExecute = true, so the child's stdout cannot be
        // redirected. Instead the child writes one result per path to this temp file, which
        // the unelevated parent reads back below. The file lives in the user's own temp
        // directory; the elevated child runs as the same user (a higher-integrity token of
        // the same account), so the file it writes stays readable here. It is deleted in the
        // finally regardless of outcome.
        var resultsPath = Path.Combine(Path.GetTempPath(), $"pathhide-apply-{Guid.NewGuid():N}.jsonl");

        var psi = new ProcessStartInfo(exePath)
        {
            // The runas shell verb is what triggers the UAC elevation prompt; the "apply" subcommand
            // and its options (built below) are the child's own command line, a separate concern.
            Verb = "runas",
            UseShellExecute = true,
        };
        foreach (var arg in ElevatedApplyCommand.BuildArguments(hideList, systemList, showList, resultsPath))
            psi.ArgumentList.Add(arg);

        var totalPaths = hideList.Count + systemList.Count + showList.Count;

        try
        {
            Log.Info("elevated apply: launching", new
            {
                totalPaths,
                hide = hideList.Count,
                system = systemList.Count,
                show = showList.Count,
            });
            using var process = Process.Start(psi);
            if (process is null)
            {
                Log.Error("elevated apply: process did not start");
                return new ElevatedApplyOutcome(-1, EmptyResults);
            }

            await process.WaitForExitAsync();
            var results = ReadResults(resultsPath);
            Log.Info("elevated apply: exited", new { exitCode = process.ExitCode, reported = results.Count });
            return new ElevatedApplyOutcome(process.ExitCode, results);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("elevated apply: UAC cancelled by user");
            return new ElevatedApplyOutcome(-1, EmptyResults);
        }
        catch (Exception ex)
        {
            Log.Error("elevated apply: launch failed", ex);
            return new ElevatedApplyOutcome(-1, EmptyResults);
        }
        finally
        {
            TryDeleteResults(resultsPath);
        }
    }

    private static IReadOnlyDictionary<string, bool> ReadResults(string path)
    {
        try
        {
            if (!File.Exists(path))
                return EmptyResults;

            // Each reported path is keyed by the exact string the parent handed the child
            // (which the child echoes back), so an ordinal match is exact.
            var byPath = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var result in ElevatedApplyResults.Parse(File.ReadAllText(path)))
                byPath[result.Path] = result.Ok;
            return byPath;
        }
        catch (Exception ex)
        {
            Log.Error("elevated apply: failed to read results file", ex, new { path });
            return EmptyResults;
        }
    }

    private static void TryDeleteResults(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug("elevated apply: failed to delete results file", ex, new { path });
        }
    }
}
