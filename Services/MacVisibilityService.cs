using System;
using System.Diagnostics;
using System.IO;
using PathHide.Models;
using Serilog;

namespace PathHide.Services;

public sealed class MacVisibilityService : IVisibilityService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MacVisibilityService>();

    public PathInspection Inspect(string path)
    {
        try
        {
            if (!ExistsAnything(path))
            {
                if (IsAncestorAccessible(path))
                    return new PathInspection(ActualState.Missing, ItemKind.Unknown);

                return new PathInspection(ActualState.Unreachable, ItemKind.Unknown);
            }

            var kind = DetectKind(path);
            var hidden = IsFinderHidden(path);
            var state = hidden ? ActualState.Hidden : ActualState.Visible;

            return new PathInspection(state, kind);
        }
        catch (UnauthorizedAccessException)
        {
            return new PathInspection(ActualState.Unreachable, ItemKind.Unknown);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to inspect {Path}", path);
            return new PathInspection(ActualState.Error, ItemKind.Unknown);
        }
    }

    public void Hide(string path)
    {
        Log.Information("Hiding {Path} via chflags hidden", path);
        RunChflags("hidden", path);
    }

    public void Show(string path)
    {
        Log.Information("Showing {Path} via chflags nohidden", path);
        RunChflags("nohidden", path);
    }

    private static bool IsFinderHidden(string path)
    {
        // stat -f %Xf gives hex flags on macOS; UF_HIDDEN = 0x8000
        var psi = new ProcessStartInfo("stat", $"-f %Xf \"{path}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to start stat");

        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"stat exited with code {process.ExitCode}");

        var flags = Convert.ToUInt32(output, 16);
        return (flags & 0x8000) != 0;
    }

    private static void RunChflags(string flag, string path)
    {
        var psi = new ProcessStartInfo("chflags", $"{flag} \"{path}\"")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to start chflags");

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"chflags {flag} failed: {stderr.Trim()}");
    }

    private static bool ExistsAnything(string path)
    {
        // File.Exists and Directory.Exists both return false for symlinks to missing targets,
        // so also check via filesystem entry enumeration
        return Path.Exists(path);
    }

    private static bool IsAncestorAccessible(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
            return false;

        try
        {
            return Directory.Exists(parent);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Cannot check ancestor accessibility for {Path}", path);
            return false;
        }
    }

    private static ItemKind DetectKind(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);

            if (attrs.HasFlag(FileAttributes.ReparsePoint))
                return ItemKind.Symlink;

            if (attrs.HasFlag(FileAttributes.Directory))
                return ItemKind.Directory;

            return ItemKind.File;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Cannot detect item kind for {Path}", path);
            return ItemKind.Unknown;
        }
    }
}
