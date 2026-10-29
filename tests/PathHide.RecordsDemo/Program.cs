using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Threading;
using PathHide;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.ViewModels;
using PathHide.Views;

// Opens PathHide's real Records window over a throwaway data folder seeded with 10,000 records, and logs
// a few more every second, so its paging and live updates can be watched by eye: scroll down and the
// list stays put while records arrive; return to the top and they appear. Closing the window ends the
// demo and deletes the folder. The developer's own data is never touched.
//
// Run from the repository root:  dotnet run --project tests/PathHide.RecordsDemo

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

AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();

var owner = new MainWindowViewModel(new VisibilityCalls(new MacVisibilityService()), new PathListStore(), new SettingsStore(),
    new AppSettings(), new JsonStore<AppState>(AppState.FileName, "state", FormatVersions.State, recordBackup: false),
    new AppState(), []);
var window = new RecordsWindow(new RecordsViewModel(Log.CreateRecordsReader(), Log.Session), owner);
using var closed = new CancellationTokenSource();
window.Closed += (_, _) => closed.Cancel();
window.Show();

var arrived = 0;
using (new Timer(_ =>
{
    var count = Interlocked.Increment(ref arrived);
    if (count % 5 == 0)
        Log.Warn("demo: live record", new { count });
    else
        Log.Info("demo: live record", new { count });
}, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(350)))
{
    Dispatcher.UIThread.MainLoop(closed.Token);
}

Log.Shutdown();
Directory.Delete(root, recursive: true);
