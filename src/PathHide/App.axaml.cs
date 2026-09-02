using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.ViewModels;
using PathHide.Views;

namespace PathHide;

public partial class App : Application
{
    internal static string? StartupFailureMessage { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (StartupFailureMessage is { } startupFailure)
            {
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    "PathHide could not start",
                    startupFailure);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // Builds the view model, which materializes config.json (CreateIfMissing) before the window.
            // The data backup is now write-through — recorded the instant each managed save's atomic rename
            // lands (see JsonStore/BackupStore) — so there is no startup backup pass to kick off here.
            //
            // If an unreadable store cannot be set aside, stop before any defaults can overwrite it.
            MainWindowViewModel viewModel;
            try
            {
                viewModel = CreateMainViewModel();
                // paths.json normally loads from the window's Loaded handler. Do
                // the read now so its recovery and any failed quarantine share
                // the same startup report/catch as config.json.
                viewModel.LoadPersistedState();
            }
            catch (Storage.PathListUnreadableException)
            {
                // The list WAS set aside successfully — its bytes are safe. The
                // halt is not about the move failing; it is that opening with an
                // empty list would look exactly like losing it, and the first add
                // would then write a fresh file containing only that entry.
                var quarantined = Storage.QuarantineJournal.Drain();
                Log.Warn("startup: the path list could not be read; halting rather than starting empty",
                    new { quarantined = string.Join(", ", quarantined.Select(q => q.Path)) });
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    "PathHide could not read your path list",
                    FailurePresentation.PathListStartup());
                RegisterOwnerActivation(desktop.MainWindow);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            catch (Exception ex)
            {
                Log.Error("startup: a settings file could not be read or set aside", ex);
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    "PathHide could not start",
                    FailurePresentation.Startup());
                RegisterOwnerActivation(desktop.MainWindow);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.MainWindow = mainWindow;
            RegisterOwnerActivation(mainWindow);

            // Report material recovery once the main window can own the dialog.
            mainWindow.Opened += async (_, _) =>
            {
                var quarantined = Storage.QuarantineJournal.Drain();
                if (quarantined.Count > 0)
                {
                    var (title, body) = Storage.QuarantineJournal.Describe(quarantined);
                    await Views.NoticeDialog.ShowAsync(mainWindow, title, body);
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RegisterOwnerActivation(Window window)
    {
        SingleInstanceLease.RegisterOwnerActivationHandler(() => Dispatcher.UIThread.Post(() =>
        {
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            if (!window.IsVisible)
                window.Show();
            window.Activate();
        }));
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
        // Settings are re-derivable, so an unreadable config.json correctly falls back to
        // defaults; the recovery notice tells the user it happened. The path list does NOT —
        // see LoadPersistedState.
        var settings = settingsStore.Load().Value;

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

        // Key effective configuration at startup (the conventions' baseline): every user-tunable
        // setting, not a subset. Logging only the hide mode meant a session log could not answer
        // which UI font was in effect — the one setting that plausibly explains a rendering
        // complaint.
        Log.Info("config", new
        {
            hideMode = settings.WindowsHideMode,
            uiFontFamily = settings.UiFontFamily,
        });

        IVisibilityService visibilityService = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsVisibilityService(() => settings.WindowsHideMode)
            : new MacVisibilityService();

        return new MainWindowViewModel(visibilityService, pathListStore, settingsStore, settings);
    }
}
