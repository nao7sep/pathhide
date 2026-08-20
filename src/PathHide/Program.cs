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
        if (args.Length > 0 && args[0] == ElevatedApplyCommand.Subcommand)
            return RunApplyMode(args);

        // Resolve and create the storage root before anything else reads or writes it.
        // An unusable PATHHIDE_HOME (or an unwritable home) is a startup error we report
        // and STOP on — never a silent fallback that lets the app run unable to persist.
        // This runs before Log.Start because the log directory itself lives under the
        // root, and outside the try below so a bad root can never reach the UI.
        try
        {
            StorageRoot.EnsureExists();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "PathHide cannot start: its storage location could not be created. " + ex.Message);
            return 1;
        }

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
        var clean = true;
        try
        {
            // The same baseline the GUI writes. This log sits beside the GUI's, and it is the
            // one session where "which binary ran elevated, and did it finish?" is the question
            // you need answered — so it carries the build and the outcome too, rather than three
            // apply lines with no version and no ending.
            Log.Info("startup", new
            {
                mode = "apply",
                version = AppVersion(),
                os = RuntimeInformation.OSDescription,
                arch = RuntimeInformation.OSArchitecture,
                storageDir = StorageRoot.Directory,
                debugLogging = Log.DebugEnabled,
            });

            var hideOpt = new Option<string[]>(ElevatedApplyCommand.HideOption)
                { AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.ZeroOrMore };
            var systemOpt = new Option<string[]>(ElevatedApplyCommand.SystemOption)
                { AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.ZeroOrMore };
            var showOpt = new Option<string[]>(ElevatedApplyCommand.ShowOption)
                { AllowMultipleArgumentsPerToken = true, Arity = ArgumentArity.ZeroOrMore };
            // Optional sink for per-path results: stdout cannot be redirected across the
            // runas boundary, so the launcher passes a temp file path here and reads it back.
            // Absent (e.g. a standalone CLI invocation) means write nothing.
            var resultsOpt = new Option<string?>(ElevatedApplyCommand.ResultsOption);

            var applyCmd = new Command(ElevatedApplyCommand.Subcommand, "Apply file attributes in batch");
            applyCmd.Add(hideOpt);
            applyCmd.Add(systemOpt);
            applyCmd.Add(showOpt);
            applyCmd.Add(resultsOpt);

            applyCmd.SetAction((ParseResult result) =>
            {
                var toHide   = result.GetValue(hideOpt)   ?? [];
                var toSystem = result.GetValue(systemOpt) ?? [];
                var toShow   = result.GetValue(showOpt)   ?? [];

                // Each result is appended the moment its path is done, so the file is a true
                // running record. Accumulating in memory and writing once at the end meant that
                // in the exact scenario the parent's timeout exists for — a stall inside
                // SetAttributes on a share that stopped answering — the file did not exist yet,
                // so every path the child HAD already changed was reported to the user as an
                // error. The parent's reader tolerates a truncated final line, which is what a
                // killed child leaves behind.
                var resultsPath = result.GetValue(resultsOpt);
                var results = new List<PathApplyResult>(toHide.Length + toSystem.Length + toShow.Length);
                results.AddRange(ApplyFileAttributes(toHide,   hide: true,  system: false, resultsPath));
                results.AddRange(ApplyFileAttributes(toSystem, hide: true,  system: true,  resultsPath));
                results.AddRange(ApplyFileAttributes(toShow,   hide: false, system: false, resultsPath));

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
            clean = false;
            return 3;
        }
        finally
        {
            Log.Info("shutdown", new { clean });
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
    private static List<PathApplyResult> ApplyFileAttributes(
        string[] paths, bool hide, bool system, string? resultsPath)
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
                var attrs = WindowsFileVisibility.ApplyVisibility(
                    File.GetAttributes(path), hide, system);
                File.SetAttributes(path, attrs);
                var ok = new PathApplyResult(path, Ok: true);
                results.Add(ok);
                AppendResult(resultsPath, ok);
            }
            catch (Exception ex)
            {
                // These paths reached the elevated pass precisely because the
                // unelevated attempt hit access-denied, so a failure here is
                // unexpected and gets a full error — not a silent swallow.
                Log.Error("apply: failed to set attributes", ex, new { path, hide, system });
                var bad = new PathApplyResult(path, Ok: false);
                results.Add(bad);
                AppendResult(resultsPath, bad);
                failed++;
            }
        }

        Log.Info("apply: done", new { ok = paths.Length - failed, failed });
        return results;
    }

    /// <summary>
    /// Appends one result to the parent's results file, if it asked for one.
    /// </summary>
    /// <remarks>
    /// Per path rather than per run: the file is the only channel back across the runas
    /// boundary (stdout cannot be redirected through the shell verb), and a run that is killed
    /// or stalls partway must still account for what it did. A failed append degrades the
    /// verdict rather than breaking the run — the parent falls back to re-inspecting any path
    /// it gets no result for.
    ///
    /// not recorded: a transient elevated-IPC file in the OS temp directory, not under
    /// ~/.pathhide/, and never reloaded as state — neither managed data nor written through the
    /// atomic-write choke point (data-backup conventions).
    /// </remarks>
    private static void AppendResult(string? resultsPath, PathApplyResult result)
    {
        if (string.IsNullOrEmpty(resultsPath))
            return;

        try
        {
            File.AppendAllText(resultsPath, ElevatedApplyResults.SerializeLine(result));
        }
        catch (Exception ex)
        {
            Log.Error("apply: failed to append to the results file", ex, new { resultsPath });
        }
    }
}
