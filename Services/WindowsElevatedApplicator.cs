using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Serilog;

namespace PathHide.Services;

[SupportedOSPlatform("windows")]
public static class WindowsElevatedApplicator
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(WindowsElevatedApplicator));

    public static async Task<int> ApplyAsync(
        IEnumerable<string> toHide,
        IEnumerable<string> toHideWithSystem,
        IEnumerable<string> toShow)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Error("Cannot determine process path for elevated apply");
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
            Log.Information("Launching elevated process ({TotalPaths} paths)", totalPaths);
            using var process = Process.Start(psi);
            if (process is null)
            {
                Log.Error("Elevated process did not start");
                return -1;
            }

            await process.WaitForExitAsync();
            Log.Information("Elevated process exited with code {Code}", process.ExitCode);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Information("UAC prompt cancelled by user");
            return -1;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch elevated process");
            return -1;
        }
    }
}
