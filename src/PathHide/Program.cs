using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using PathHide.I18n;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Views;

namespace PathHide;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == ElevatedApplyCommand.Subcommand)
            return RunApplyMode(args);

        // The interface language, before anything can draw and before Avalonia creates the macOS
        // application object, which settles the language of the menu items AppKit contributes itself.
        // It reads the saved preference straight from config.json and falls back to the computer's own
        // languages, so it holds on the startup-failure path below, where there is no usable storage.
        App.ComputerLanguages = LanguageBootstrap.Start();

        // Resolve and create the storage root before anything else reads or writes it.
        // An unusable PATHHIDE_DATA_DIR (or an unwritable home) is a startup error we report
        // and STOP on — never a silent fallback that lets the app run unable to persist.
        // This runs before Log.Start because the log directory itself lives under the
        // root, and outside the try below so a bad root can never reach the UI.
        try
        {
            StorageRoot.EnsureExists();
        }
        catch (Exception ex)
        {
            // Diagnostics on stderr, which stay English with the exception's own message: this is the
            // log channel, not an interface surface. What the reader sees is the notice window.
            Console.Error.WriteLine(
                "PathHide cannot start: its storage location could not be created. " + ex.Message);
            App.StartupFailureMessage = ViewModels.FailurePresentation.StartupStorage();
            _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 1;
        }

        if (!SingleInstanceLease.TryAcquire(StorageRoot.Directory, out var instanceLease))
        {
            Console.Error.WriteLine("PathHide is already running; activated the existing window.");
            return 0;
        }
        using var ownedInstance = instanceLease;

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
            .With(MacMenuBar.PlatformOptions())
            .WithInterFont()
            .LogToTrace();

    private static string AppVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

    private static int RunApplyMode(string[] args)
    {
        // Parsing writes nothing, so it runs before the logger opens: the parent's storage root has to
        // be adopted first, so this session's log lands in the same tree as the GUI's. It arrives as an
        // argument because the runas verb forces UseShellExecute, which forbids setting the child's
        // environment block — without it a root relocated by PATHHIDE_DATA_DIR would be re-resolved to the
        // default here, splitting the log trail for exactly the access-denied failures this pass
        // exists to diagnose.
        var invocation = ElevatedApplyCommand.ParseArguments(args);
        if (!string.IsNullOrWhiteSpace(invocation?.StorageRoot))
            Environment.SetEnvironmentVariable(StorageRoot.HomeEnvironmentVariable, invocation.StorageRoot);

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

            if (invocation is null)
            {
                Log.Error("apply mode: invalid arguments");
                clean = false;
                return 2;
            }

            var request = ElevatedApplyCommand.ParseRequest(File.ReadAllText(invocation.RequestPath));

            // Each result is written and flushed the moment its path is done, so the file is a true
            // running record: when the parent stops waiting on a child stalled inside SetAttributes on
            // a share that stopped answering, every path already changed is still reported. The file
            // is created here, never reused (a pre-existing file at this name is refused rather than
            // written through), and held open without delete sharing for the whole run, which is
            // what tells the parent's cleanup that this child is still running.
            using var results = invocation.ResultsPath is { Length: > 0 } resultsPath
                ? new StreamWriter(
                    new FileStream(resultsPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                : null;

            var failed = ApplyFileAttributes(request.ToHide, hide: true, system: false, results)
                | ApplyFileAttributes(request.ToHideWithSystem, hide: true, system: true, results)
                | ApplyFileAttributes(request.ToShow, hide: false, system: false, results);

            // The per-path file is the authoritative channel; the exit code stays a coarse
            // 0 = all ok / 1 = some failed signal for callers and logs.
            return failed ? 1 : 0;
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

    /// <returns>Whether any path failed.</returns>
    /// <remarks>
    /// <c>File.GetAttributes</c>/<c>File.SetAttributes</c> operate on the reparse point
    /// itself, not its target (verified on Windows for symlinks and junctions, elevated and
    /// not). So a path swapped for a junction between the unelevated inspect and this elevated
    /// write can only have its own attributes changed — it cannot redirect this admin write
    /// onto the link's target. Keep both calls path-based for that reason; do not switch to a
    /// follow-based API or add reparse-handle machinery to "harden" a hazard that cannot occur.
    /// </remarks>
    private static bool ApplyFileAttributes(
        IReadOnlyList<string> paths, bool hide, bool system, StreamWriter? results)
    {
        if (paths.Count == 0)
            return false;

        // Loop coverage per the conventions: one info line for the intent, one for the
        // outcome, and one error per failure — never one line per successful item.
        Log.Info("apply: start", new { count = paths.Count, hide, system });

        var failed = 0;
        foreach (var path in paths)
        {
            try
            {
                var attrs = WindowsFileVisibility.ApplyVisibility(
                    File.GetAttributes(path), hide, system);
                // not recorded: this changes only external filesystem metadata; paths.json
                // records the user's desired visibility and tracked-path identity.
                File.SetAttributes(path, attrs);
                WriteResult(results, new PathApplyResult(path, Ok: true));
            }
            catch (Exception ex)
            {
                // These paths reached the elevated pass precisely because the
                // unelevated attempt hit access-denied, so a failure here is
                // unexpected and gets a full error — not a silent swallow.
                Log.Error("apply: failed to set attributes", ex, new { path, hide, system });
                WriteResult(results, new PathApplyResult(path, Ok: false));
                failed++;
            }
        }

        Log.Info("apply: done", new { ok = paths.Count - failed, failed });
        return failed > 0;
    }

    private static void WriteResult(StreamWriter? results, PathApplyResult result)
    {
        if (results is null)
            return;

        try
        {
            // not recorded: a transient elevated-IPC result in the OS temp directory, never
            // reloaded as managed state.
            results.Write(ElevatedApplyResults.SerializeLine(result));
            results.Flush();
        }
        catch (Exception ex)
        {
            Log.Error("apply: failed to write to the results file", ex);
        }
    }
}
