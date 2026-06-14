using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using PathHide.Services;
using PathHide.Storage;
using System.CommandLine;

namespace PathHide;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "apply")
            return RunApplyMode(args);

        // One JSON-Lines file per launch under the app's logs directory; the logger
        // installs its own crash hooks and console fallback.
        Log.Start(StorageRoot.LogsDirectory);
        var clean = true;
        try
        {
            Log.Info("startup", new
            {
                version = AppVersion(),
                os = RuntimeInformation.OSDescription,
                arch = RuntimeInformation.OSArchitecture,
                storageDir = StorageRoot.Directory,
                debugLogging = Log.DebugEnabled,
            });
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // The "why" of a forced shutdown; the shutdown line below records that it
            // was not clean.
            Log.Error("fatal: terminated unexpectedly", ex);
            clean = false;
            return 1;
        }
        finally
        {
            Log.Info("shutdown", new { clean });
            Log.Shutdown();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static string AppVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    private static int RunApplyMode(string[] args)
    {
        // The elevated apply pass is a genuinely separate OS process, so it gets its
        // own per-session log file (co-located with the GUI process's logs).
        Log.Start(StorageRoot.LogsDirectory);
        try
        {
            var hideOpt = new Option<string[]>("--hide")
                { AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.ZeroOrMore };
            var systemOpt = new Option<string[]>("--system")
                { AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.ZeroOrMore };
            var showOpt = new Option<string[]>("--show")
                { AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.ZeroOrMore };
            // Optional sink for per-path results: stdout cannot be redirected across the
            // runas boundary, so the launcher passes a temp file path here and reads it back.
            // Absent (e.g. a standalone CLI invocation) means write nothing.
            var resultsOpt = new Option<string?>("--results");

            var applyCmd = new Command("apply", "Apply file attributes in batch");
            applyCmd.Add(hideOpt);
            applyCmd.Add(systemOpt);
            applyCmd.Add(showOpt);
            applyCmd.Add(resultsOpt);

            applyCmd.SetAction((ParseResult result) =>
            {
                var toHide   = result.GetValue(hideOpt)   ?? [];
                var toSystem = result.GetValue(systemOpt) ?? [];
                var toShow   = result.GetValue(showOpt)   ?? [];

                var results = new List<PathApplyResult>(toHide.Length + toSystem.Length + toShow.Length);
                results.AddRange(ApplyFileAttributes(toHide,   hide: true,  system: false));
                results.AddRange(ApplyFileAttributes(toSystem, hide: true,  system: true));
                results.AddRange(ApplyFileAttributes(toShow,   hide: false, system: false));

                WriteResults(result.GetValue(resultsOpt), results);

                // The per-path file is the authoritative channel; the exit code stays a coarse
                // 0 = all ok / 1 = some failed signal for callers and logs.
                return results.Any(r => !r.Ok) ? 1 : 0;
            });

            var root = new RootCommand("PathHide apply mode");
            root.Add(applyCmd);

            var parseResult = root.Parse(args);
            return parseResult.Invoke(parseResult.InvocationConfiguration);
        }
        catch (Exception ex)
        {
            Log.Error("apply mode: failed", ex);
            return 3;
        }
        finally
        {
            Log.Shutdown();
        }
    }

    /// <returns>One <see cref="PathApplyResult"/> per input path, in input order.</returns>
    /// <remarks>
    /// <c>File.GetAttributes</c>/<c>File.SetAttributes</c> operate on the reparse point
    /// itself, not its target (verified on Windows for symlinks and junctions, elevated and
    /// not). So a path swapped for a junction between the unelevated inspect and this elevated
    /// write can only have its own attributes changed — it cannot redirect this admin write
    /// onto the link's target. Keep both calls path-based for that reason; do not switch to a
    /// follow-based API or add reparse-handle machinery to "harden" a hazard that cannot occur.
    /// </remarks>
    private static List<PathApplyResult> ApplyFileAttributes(string[] paths, bool hide, bool system)
    {
        var results = new List<PathApplyResult>(paths.Length);
        if (paths.Length == 0)
            return results;

        // Loop coverage per the conventions: one info line for the intent, one for the
        // outcome, and one error per failure — never one line per successful item.
        Log.Info("apply: start", new { count = paths.Length, hide, system });

        var failed = 0;
        foreach (var path in paths)
        {
            try
            {
                var attrs = File.GetAttributes(path);

                if (hide) attrs |= FileAttributes.Hidden;
                else attrs &= ~FileAttributes.Hidden;

                if (system) attrs |= FileAttributes.System;
                else attrs &= ~FileAttributes.System;

                File.SetAttributes(path, attrs);
                results.Add(new PathApplyResult(path, Ok: true));
            }
            catch (Exception ex)
            {
                // These paths reached the elevated pass precisely because the
                // unelevated attempt hit access-denied, so a failure here is
                // unexpected and gets a full error — not a silent swallow.
                Log.Error("apply: failed to set attributes", ex, new { path, hide, system });
                results.Add(new PathApplyResult(path, Ok: false));
                failed++;
            }
        }

        Log.Info("apply: done", new { ok = paths.Length - failed, failed });
        return results;
    }

    private static void WriteResults(string? resultsPath, List<PathApplyResult> results)
    {
        if (string.IsNullOrEmpty(resultsPath))
            return;

        try
        {
            File.WriteAllText(resultsPath, ElevatedApplyResults.Serialize(results));
        }
        catch (Exception ex)
        {
            // The launcher falls back to re-inspection for any path it gets no result for,
            // so a failed write degrades the verdict rather than breaking it.
            Log.Error("apply: failed to write results file", ex, new { resultsPath });
        }
    }
}
