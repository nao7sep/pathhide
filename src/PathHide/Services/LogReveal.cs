using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PathHide.Storage;

namespace PathHide.Services;

internal enum LogRevealTargetKind
{
    File,
    Directory,
}

internal readonly record struct LogRevealTarget(string Path, LogRevealTargetKind Kind);

/// <summary>
/// Best-effort "show me the log" helper. Reveals the records database, which holds the log, in the host
/// platform's file manager (Finder on macOS, Explorer on Windows), or opens the folder that would hold it
/// when there is none yet.
/// </summary>
public static class LogReveal
{
    public static bool Reveal()
    {
        try
        {
            var target = SelectTarget(StorageRoot.RecordsFile);
            if (target.Kind == LogRevealTargetKind.File)
                RevealInFileManager(target.Path);
            else
                OpenDirectoryInFileManager(target.Path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("reveal log: failed", ex);
            return false;
        }
    }

    internal static LogRevealTarget SelectTarget(string recordsFile) =>
        File.Exists(recordsFile)
            ? new LogRevealTarget(recordsFile, LogRevealTargetKind.File)
            : new LogRevealTarget(Path.GetDirectoryName(recordsFile)!, LogRevealTargetKind.Directory);

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
