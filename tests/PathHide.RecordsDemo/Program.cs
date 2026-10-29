using System;
using System.IO;
using System.Threading;
using Avalonia;
using PathHide;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.ViewModels;
using PathHide.Views;

// Runs PathHide over a throwaway data folder seeded with 10,000 records, opens its real Records window
// beside the main window, and logs a few more records every second, so the window's paging and live
// updates can be watched by eye: scroll down and the list stays put while records arrive; return to the
// top and they appear. Quitting PathHide (its menu, Cmd+Q, the Dock or the launchers) ends the demo and
// deletes the folder. The developer's own data is never touched.
//
// Run from the repository root:  tests/PathHide.RecordsDemo/run.command

const int Seeded = 10_000;
var root = Directory.CreateTempSubdirectory("pathhide-records-demo-").FullName;
Environment.SetEnvironmentVariable(StorageRoot.DataDirEnvironmentVariable, root);
Console.WriteLine($"Records demo over {root}");

var store = RecordsStore.TryOpen(StorageRoot.RecordsFile, out var failure) ?? throw failure!;
var start = DateTimeOffset.UtcNow.AddSeconds(-Seeded);
const string SeedSession = "2026-01-01T00:00:00.000Z";
string[] levels = ["info", "info", "info", "debug", "warn", "error"];
for (var index = 0; index < Seeded; index++)
{
    store.Write(new LogEntry(SeedSession, FileTimestamp.SerializedStamp(start.AddSeconds(index)), levels[index % levels.Length],
        $"seeded record {index}", null, null));
}
Log.Start(store, StorageRoot.LogsDirectory);

var arrived = 0;
using var arrivals = new Timer(_ =>
{
    var count = Interlocked.Increment(ref arrived);
    if (count % 5 == 0)
        Log.Warn("demo: live record", new { count });
    else
        Log.Info("demo: live record", new { count });
}, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(350));

// The app's own startup shows its main window over the throwaway folder; the Records window opens beside
// it. On macOS a quit ends the process as the lifetime exits, so the folder goes there.
// As the app builds itself (PathHide's Program.BuildAvaloniaApp), its own macOS menu bar in place of Avalonia's.
return AppBuilder.Configure<App>().UsePlatformDetect().With(new MacOSPlatformOptions { DisableNativeMenus = true }).WithInterFont()
    .StartWithClassicDesktopLifetime(args, lifetime =>
{
    lifetime.Startup += (_, _) =>
    {
        var owner = new MainWindowViewModel(new VisibilityCalls(new MacVisibilityService()), new PathListStore(), new SettingsStore(),
            new AppSettings(), new JsonStore<AppState>(AppState.FileName, "state", FormatVersions.State, recordBackup: false),
            new AppState(), []);
        new RecordsWindow(new RecordsViewModel(Log.CreateRecordsReader(), Log.Session), owner).Show();
    };
    lifetime.Exit += (_, _) =>
    {
        arrivals.Dispose();
        Log.Shutdown();
        Directory.Delete(root, recursive: true);
    };
});
