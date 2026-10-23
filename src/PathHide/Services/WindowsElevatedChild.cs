using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace PathHide.Services;

/// <summary>The Windows <see cref="ElevatedChildLauncher"/>: this executable, run through the UAC prompt.</summary>
public static class WindowsElevatedChild
{
    [SupportedOSPlatform("windows")]
    public static async Task<Task<int>?> LaunchAsync(IReadOnlyList<string> arguments)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Error("elevated apply: no process path");
            return null;
        }

        var psi = CreateStartInfo(exePath, arguments);

        Process? process;
        try
        {
            // Off the UI thread: with the runas verb Process.Start returns only once the user answers
            // the consent prompt, and with the secure desktop off the window would otherwise stop
            // repainting and be marked "(Not Responding)".
            process = await Task.Run(() => Process.Start(psi));
        }
        catch (Exception ex) when (IsDeclined(ex))
        {
            throw new ElevationDeclinedException();
        }
        catch (Exception ex)
        {
            Log.Error("elevated apply: launch failed", ex);
            return null;
        }

        if (process is null)
        {
            Log.Error("elevated apply: process did not start");
            return null;
        }

        return WaitForExitAsync(process);
    }

    /// <summary>
    /// The child's start: the runas shell verb is what raises the UAC prompt. It forces UseShellExecute,
    /// so the child's output cannot be redirected and results come back through a file instead. Each
    /// argument is passed whole, so a path with spaces stays one argument.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string exePath, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            Verb = "runas",
            UseShellExecute = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        return psi;
    }

    /// <summary>Whether the start failed because the user declined the UAC prompt (ERROR_CANCELLED).</summary>
    internal static bool IsDeclined(Exception exception) =>
        exception is Win32Exception { NativeErrorCode: 1223 };

    private static async Task<int> WaitForExitAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }
}
