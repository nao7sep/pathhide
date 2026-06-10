using System;
using System.IO;
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

            var applyCmd = new Command("apply", "Apply file attributes in batch");
            applyCmd.Add(hideOpt);
            applyCmd.Add(systemOpt);
            applyCmd.Add(showOpt);

            applyCmd.SetAction((ParseResult result) =>
            {
                var toHide   = result.GetValue(hideOpt)   ?? [];
                var toSystem = result.GetValue(systemOpt) ?? [];
                var toShow   = result.GetValue(showOpt)   ?? [];

                var failed = ApplyFileAttributes(toHide,   hide: true,  system: false)
                           + ApplyFileAttributes(toSystem, hide: true,  system: true)
                           + ApplyFileAttributes(toShow,   hide: false, system: false);
                return failed > 0 ? 1 : 0;
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

    /// <returns>The number of paths that could not be updated.</returns>
    private static int ApplyFileAttributes(string[] paths, bool hide, bool system)
    {
        if (paths.Length == 0)
            return 0;

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
            }
            catch (Exception ex)
            {
                // These paths reached the elevated pass precisely because the
                // unelevated attempt hit access-denied, so a failure here is
                // unexpected and gets a full error — not a silent swallow.
                Log.Error("apply: failed to set attributes", ex, new { path, hide, system });
                failed++;
            }
        }

        Log.Info("apply: done", new { ok = paths.Length - failed, failed });
        return failed;
    }
}
