using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PathHide.Storage;
using Serilog;

namespace PathHide.Services;

/// <summary>
/// Best-effort "show me the log" helper. Locates the most recently written
/// rolled log file under <see cref="StorageRoot.LogsDirectory"/> and reveals it
/// in the host platform's file manager (Finder on macOS, Explorer on Windows).
/// Falls back to opening the logs directory if no log file is present.
/// </summary>
public static class LogReveal
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(LogReveal));

    public static void Reveal()
    {
        try
        {
            var dir = StorageRoot.LogsDirectory;
            Directory.CreateDirectory(dir);

            var current = TryFindMostRecentLog(dir);
            if (current is not null)
                RevealInFileManager(current);
            else
                OpenDirectoryInFileManager(dir);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to reveal log file");
        }
    }

    private static string? TryFindMostRecentLog(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("pathhide-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()
                ?.FullName;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not enumerate logs in {Dir}", dir);
            return null;
        }
    }

    private static void RevealInFileManager(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // `open -R` selects and reveals the item in Finder.
            var psi = new ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(path);
            Process.Start(psi)?.Dispose();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // explorer /select,PATH opens the parent folder and selects the file.
            // explorer.exe parses this as a single token; manual quoting is the
            // canonical form because ArgumentList's auto-quoting can confuse it.
            var psi = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = false,
                Arguments = $"/select,\"{path}\"",
            };
            Process.Start(psi)?.Dispose();
        }
        else
        {
            OpenDirectoryInFileManager(Path.GetDirectoryName(path) ?? path);
        }
    }

    private static void OpenDirectoryInFileManager(string dir)
    {
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
    }
}
