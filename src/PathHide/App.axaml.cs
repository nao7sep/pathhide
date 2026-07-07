using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.ViewModels;
using PathHide.Views;

namespace PathHide;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Builds the view model, which materializes config.json (CreateIfMissing) before the window.
            // The data backup is now write-through — recorded the instant each managed save's atomic rename
            // lands (see JsonStore/BackupStore) — so there is no startup backup pass to kick off here.
            desktop.MainWindow = new MainWindow
            {
                DataContext = CreateMainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Composition root: builds persistence, the OS-appropriate visibility service,
    /// and the view model. Settings are loaded here because the Windows service closes
    /// over the loaded instance to read the current hide mode; path entries are loaded
    /// later, when the window calls <see cref="MainWindowViewModel.Initialize"/>.
    /// </summary>
    private static MainWindowViewModel CreateMainViewModel()
    {
        var pathListStore = new JsonStore<List<PathEntry>>("paths.json", "paths");
        var settingsStore = new JsonStore<AppSettings>("config.json", "settings");
        var settings = settingsStore.Load();

        // Create config.json on first run so the settings file exists on disk immediately, not only
        // after the first save (storage-path conventions, "Materializing settings on first run"). This
        // runs here — right after the load populates `settings`, before the visibility service and the
        // view model read it — and only creates the file when absent, so an existing file is never
        // overwritten. paths.json is user content (empty by default), not a defaults-
        // bearing settings file, so it is left to be created when the user first adds a path. A first-run
        // write failure is logged and tolerated rather than crashing startup.
        try
        {
            settingsStore.CreateIfMissing(settings);
        }
        catch (Exception ex)
        {
            Log.Warn("config: first-run create failed", ex, new { file = "config.json" });
        }

        // Key effective configuration at startup (the conventions' baseline). The hide
        // mode is the one user-tunable setting; it lives here, loaded above.
        Log.Info("config", new { hideMode = settings.WindowsHideMode });

        IVisibilityService visibilityService = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsVisibilityService(() => settings.WindowsHideMode)
            : new MacVisibilityService();

        return new MainWindowViewModel(visibilityService, pathListStore, settingsStore, settings);
    }
}
