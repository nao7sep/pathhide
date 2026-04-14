using System;
using System.IO;
using Avalonia;
using PathHide.Storage;
using Serilog;
using System.CommandLine;

namespace PathHide;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "apply")
            return RunApplyMode(args);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(StorageRoot.Directory, "logs", "pathhide-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        try
        {
            Log.Information("PathHide starting");
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "PathHide terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.Information("PathHide shutting down");
            Log.CloseAndFlush();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int RunApplyMode(string[] args)
    {
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

                var anyFailed = ApplyFileAttributes(toHide,   hide: true,  system: false) != 0
                             || ApplyFileAttributes(toSystem, hide: true,  system: true)  != 0
                             || ApplyFileAttributes(toShow,   hide: false, system: false) != 0;
                return anyFailed ? 1 : 0;
            });

            var root = new RootCommand("PathHide apply mode");
            root.Add(applyCmd);

            var parseResult = root.Parse(args);
            return parseResult.Invoke(parseResult.InvocationConfiguration);
        }
        catch (Exception)
        {
            return 3;
        }
    }

    private static int ApplyFileAttributes(string[] paths, bool hide, bool system)
    {
        var anyFailed = false;

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
            catch
            {
                anyFailed = true;
            }
        }

        return anyFailed ? 1 : 0;
    }
}
