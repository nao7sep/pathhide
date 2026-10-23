using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace PathHide.Services;

/// <summary>Reports only a launch explicitly requested by a developer launcher, without process discovery.</summary>
internal static class LauncherRuntime
{
    internal static void Report(string[] args, bool activated)
    {
        var repoIndex = Array.IndexOf(args, "--launcher-repo");
        var tokenIndex = Array.IndexOf(args, "--launcher-token");
        if (repoIndex < 0 || tokenIndex < 0 || repoIndex + 1 >= args.Length || tokenIndex + 1 >= args.Length) return;
        var repo = args[repoIndex + 1];
        var token = args[tokenIndex + 1];
        try
        {
            var directory = Path.Combine(repo, ".git");
            if (File.ReadAllText(Path.Combine(directory, "launcher-runtime-owner")).Trim() != token) return;
            using var process = Process.GetCurrentProcess();
            var start = process.StartTime.ToUniversalTime();
            var record = new
            {
                token, pid = process.Id, startTicks = start.Ticks,
                startLabel = start.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture),
                state = activated ? "activated" : "running",
            };
            // Ephemeral launcher coordination, not app data or a backup destination.
            var destination = Path.Combine(directory, "launcher-runtime-child");
            var scratch = destination + "." + process.Id;
            File.WriteAllText(scratch, JsonSerializer.Serialize(record));
            File.Move(scratch, destination, overwrite: true);
        }
        catch (Exception ex) { _ = Log.ReportStartup("launcher: could not report the started process", ex, warning: true); }
    }
}
