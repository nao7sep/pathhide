using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace PathHide.Services;

/// <summary>The Windows <see cref="ElevatedChildLauncher"/>: this executable, run through the UAC prompt.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsElevatedChild
{
    public static async Task<Task<int>?> LaunchAsync(IReadOnlyList<string> arguments)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Error("elevated apply: no process path");
            return null;
        }

        var psi = new ProcessStartInfo(exePath)
        {
            // The runas shell verb is what raises the UAC prompt. It forces UseShellExecute, so the
            // child's output cannot be redirected: results come back through a file instead.
            Verb = "runas",
            UseShellExecute = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        Process? process;
        try
        {
            // Off the UI thread: with the runas verb Process.Start returns only once the user answers
            // the consent prompt, and with the secure desktop off the window would otherwise stop
            // repainting and be marked "(Not Responding)".
            process = await Task.Run(() => Process.Start(psi));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("elevated apply: UAC cancelled by user");
            return null;
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

    private static async Task<int> WaitForExitAsync(Process process)
    {
        using (process)
        {
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }
}
