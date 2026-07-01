using System;
using System.Threading.Tasks;
using PathHide.Backup;
using PathHide.Storage;

namespace PathHide.Services;

/// <summary>
/// Kicks off the just-in-case data backup at startup, off the UI thread, and logs its outcome. This is the
/// edge that owns threading and logging; the pass itself is <see cref="BackupEngine"/>, which does not log.
/// Best-effort: it never blocks the window, shows an error, or crashes the app.
/// </summary>
public static class BackupService
{
    /// <summary>Runs one backup pass on a background thread and logs the report. Fire-and-forget.</summary>
    public static void RunInBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var paths = new BackupPaths(StorageRoot.Directory);
                LogReport(new BackupEngine(paths).Run(DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                // The engine already captures its own failures in the report; this is the final backstop so
                // a bug here (or resolving the storage root) can never surface to the user or take down the app.
                Log.Error("backup: unexpected failure", ex);
            }
        });
    }

    private static void LogReport(BackupReport report)
    {
        foreach (var skip in report.Skips)
        {
            Log.Warn("backup: skipped a file", new { path = skip.Path, reason = skip.Reason });
        }

        if (report.IndexWasReset)
        {
            Log.Warn("backup: index was unreadable and reset; this run is a full backup");
        }

        if (report.Fatal is not null)
        {
            Log.Error("backup: run failed", report.Fatal);
            return;
        }

        if (report.NothingChanged)
        {
            Log.Debug("backup: nothing changed, no archive written");
            return;
        }

        Log.Info("backup: archive written",
            new { archive = report.ArchiveFileName, files = report.FilesArchived });
    }
}
