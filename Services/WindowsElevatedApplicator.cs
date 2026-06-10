using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace PathHide.Services;

[SupportedOSPlatform("windows")]
public static class WindowsElevatedApplicator
{
    public static async Task<int> ApplyAsync(
        IEnumerable<string> toHide,
        IEnumerable<string> toHideWithSystem,
        IEnumerable<string> toShow)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Error("elevated apply: no process path");
            return -1;
        }

        var hideList   = toHide.ToList();
        var systemList = toHideWithSystem.ToList();
        var showList   = toShow.ToList();

        var psi = new ProcessStartInfo(exePath)
        {
            Verb = "runas",
            UseShellExecute = true,
        };
        psi.ArgumentList.Add("apply");
        if (hideList.Count > 0)
        {
            psi.ArgumentList.Add("--hide");
            foreach (var p in hideList) psi.ArgumentList.Add(p);
        }
        if (systemList.Count > 0)
        {
            psi.ArgumentList.Add("--system");
            foreach (var p in systemList) psi.ArgumentList.Add(p);
        }
        if (showList.Count > 0)
        {
            psi.ArgumentList.Add("--show");
            foreach (var p in showList) psi.ArgumentList.Add(p);
        }

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
                return -1;
            }

            await process.WaitForExitAsync();
            Log.Info("elevated apply: exited", new { exitCode = process.ExitCode });
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("elevated apply: UAC cancelled by user");
            return -1;
        }
        catch (Exception ex)
        {
            Log.Error("elevated apply: launch failed", ex);
            return -1;
        }
    }
}
