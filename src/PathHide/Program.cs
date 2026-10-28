using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using PathHide.I18n;
using PathHide.Models;
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
        if (App.StartupFailureMessage is not null)
        {
            _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 1;
        }

        // Resolve and create the storage root before anything else reads or writes it.
        // An unusable PATHHIDE_DATA_DIR (or an unwritable home) is a startup error we report
        // and STOP on — never a silent fallback that lets the app run unable to persist.
        // This runs before Log.Start because the records database itself lives under the
        // root, and outside the try below so a bad root can never reach the UI.
        try
        {
            StorageRoot.EnsureExists();
        }
        catch (Exception ex)
        {
            // Diagnostics on stderr, which stay English with the exception's own message: this is the
            // log channel, not an interface surface. What the reader sees is the notice window.
            _ = Log.ReportStartup("startup: the storage location could not be created", ex);
            App.StartupFailureMessage = ViewModels.FailurePresentation.StartupStorage();
            _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 1;
        }

        SingleInstanceLease? instanceLease;
        try
        {
            if (!SingleInstanceLease.TryAcquire(StorageRoot.Directory, out instanceLease))
            {
                _ = Log.ReportStartup("PathHide is already running; requested activation of its existing window.");
                return 0;
            }
        }
        catch (Exception ex)
        {
            _ = Log.ReportStartup("startup: storage ownership could not be established", ex);
            App.StartupFailureMessage = ViewModels.FailurePresentation.StartupStorage();
            _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 1;
        }
        using var ownedInstance = instanceLease;

        // This process alone owns the records database; the logger installs its own crash hooks and
        // falls back to the session's file under logs/.
        var (records, recordsFailure) = OpenRecordsForStartup();
        Log.Start(records, StorageRoot.LogsDirectory);
        if (recordsFailure is not null)
            Log.Error("logger: could not open the records database", recordsFailure, new { file = StorageRoot.RecordsFile });
        var clean = true;
        var logClosed = false;
        void CloseLog()
        {
            if (logClosed)
                return;
            logClosed = true;
            Log.Info("shutdown", new { clean, reason = Log.ExitReason(clean) });
            Log.Shutdown();
        }

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
            // On macOS a Dock Quit, logout or shutdown ends the process as the lifetime exits, and neither
            // the finally below nor ProcessExit runs, so what the quit logged is written there first.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(
                args,
                lifetime => lifetime.Exit += (_, _) => CloseLog());
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
            CloseLog();
        }
    }

    // Records are optional: on failure the logger keeps its ordinary file/console fallback.
    private static (ILogSink? Store, Exception? Failure) OpenRecordsForStartup()
    {
        var store = RecordsStore.TryOpen(StorageRoot.RecordsFile, out var failure);
        return (store, failure);
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
        // Parsing writes nothing, so it runs before the logger starts: the parent's storage root has to
        // be adopted first, so a fallback log file lands in the same tree as the GUI's. It arrives as an
        // argument because the runas verb forces UseShellExecute, which forbids setting the child's
        // environment block (see ElevatedApplyCommand.HomeOption).
        var invocation = ElevatedApplyCommand.ParseArguments(args);
        if (!string.IsNullOrWhiteSpace(invocation?.StorageRoot))
            Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, invocation.StorageRoot);

        // This process never opens the records database: its entries go into the results file it hands
        // back, and the app imports them (logging conventions, multi-process apps). With no results file
        // they go to this session's fallback file.
        ElevatedResultsWriter? results = null;
        Exception? resultsFailure = null;
        if (invocation?.ResultsPath is { Length: > 0 } resultsPath)
        {
            try
            {
                results = ElevatedResultsWriter.Create(resultsPath);
            }
            catch (Exception ex)
            {
                resultsFailure = ex;
            }
        }

        Log.Start(results, StorageRoot.LogsDirectory, invocation?.Session);
        var clean = true;
        try
        {
            // The same baseline the GUI writes. These entries land beside the GUI's, and this is the
            // one session where "which binary ran elevated, and did it finish?" is the question
            // you need answered — so it carries the build and the outcome too, rather than three
            // apply lines with no version and no ending.
            Log.Info("startup", new
            {
                mode = "apply",
                parentPid = invocation?.Parent?.Id,
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

            if (resultsFailure is not null)
            {
                Log.Error("apply mode: could not create the results file", resultsFailure);
                clean = false;
                return 3;
            }

            // Read once: the bytes that are checked against the approved digest are the bytes applied.
            ElevatedApplyCommand.Buckets request;
            try
            {
                request = ElevatedApplyCommand.ReadRequest(File.ReadAllBytes(invocation.RequestPath), invocation.RequestDigest);
            }
            catch (InvalidDataException ex)
            {
                Log.Error("apply mode: refused a request that does not match the approved launch", ex);
                clean = false;
                return 2;
            }
            catch (FileNotFoundException) when (invocation.RequestDigest is not null)
            {
                // The GUI withdrew the request while the approval prompt was open: nothing to apply.
                Log.Info("apply mode: the request was withdrawn before this run started");
                return 0;
            }
            using var parent = invocation.Parent is { } identity ? new ApplyParentProcess(identity) : null;

            // Each result is written and flushed the moment its path is done, so when the parent stops
            // waiting on a child stalled inside SetAttributes on a share that stopped answering, every
            // path already changed is still reported.
            var work = request.ToHide.Select(path => (path, hide: true, system: SystemBit.Keep))
                .Concat(request.ToHideWithSystem.Select(path => (path, true, SystemBit.Set)))
                .Concat(request.ToHideClearingSystem.Select(path => (path, true, SystemBit.Clear)))
                .Concat(request.ToShow.Select(path => (path, false, SystemBit.Keep)))
                .Concat(request.ToShowClearingSystem.Select(path => (path, false, SystemBit.Clear)));
            var failed = ApplyTargets(work, results, parent, request.Identities, invocation.Parent is not null);

            // The per-path file is the authoritative channel; the exit code stays a coarse
            // 0 = all ok / 1 = some failed signal for callers and logs.
            return failed ? 1 : 0;
        }
        catch (OperationCanceledException)
        {
            Log.Info("apply mode: stopped because the requesting GUI exited");
            return 1;
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
            Log.Shutdown(); // closes the results file
        }
    }

    /// <returns>Whether any path failed.</returns>
    internal static bool ApplyFileAttributes(
        IReadOnlyList<string> paths, bool hide, SystemBit system, ElevatedResultsWriter? results, ApplyParentProcess? parent) =>
        ApplyTargets(paths.Select(path => (path, hide, system)), results, parent,
            new Dictionary<string, string>(), requireApproval: false);

    private static bool ApplyTargets(IEnumerable<(string Path, bool Hide, SystemBit System)> targets,
        ElevatedResultsWriter? results, ApplyParentProcess? parent,
        IReadOnlyDictionary<string, string> identities, bool requireApproval)
    {
        var failed = false;
        foreach (var target in targets)
        {
            parent?.RequireRunning();
            PathApplyResult verdict;
            try
            {
                identities.TryGetValue(target.Path, out var approved);
                if (requireApproval && approved is null)
                    throw new InvalidDataException("The approved request has no filesystem object identity.");
                var systemSet = WindowsFileVisibility.Set(target.Path, target.Hide, target.System, approved);
                verdict = new PathApplyResult(target.Path, Ok: true, systemSet);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("apply: failed to set attributes", ex, new { path = target.Path, hide = target.Hide, system = target.System });
                verdict = new PathApplyResult(target.Path, Ok: false);
                failed = true;
            }
            // A result that cannot be recorded is not a target failure: it propagates and fails the
            // process, so no further file is changed without evidence of it.
            results?.Report(verdict);
        }
        return failed;
    }
}
