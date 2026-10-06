using System;
using System.Threading.Tasks;
using System.Collections.Generic;
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
    internal static I18n.Message? StartupFailureMessage { get; set; }

    /// <summary>
    /// The computer's own languages, in order, as <c>LanguageBootstrap</c> read them before the app was
    /// built. Handed to the view model so a language saved in Settings resolves System the same way.
    /// </summary>
    internal static IReadOnlyList<string> ComputerLanguages { get; set; } = [];

    // The main window, which the app menu's About and Settings items open through. Null while a
    // startup failure is shown instead, when those items are disabled.
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        MenuGestureColumn.Install();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The app quits with its main window. The records window is a window of its own, which
            // must neither keep a closed main window's app running nor be what is left of it.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // A Dock click brings the main window back, as a second launch does, even while the
            // records window is open and so macOS sees a visible window and restores nothing itself.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen && desktop.MainWindow is { } main)
                        WindowActivation.BringBack(main);
                };
            }

            // The one macOS menu bar, set before any window so every window, a startup failure notice
            // included, shows the same bar. About and Settings are enabled while the main window is in
            // front, so never over one of its dialogs.
            MacMenuBar.Install(
                "PathHide",
                showAbout: () => _mainWindow?.ShowAboutFromMenu(),
                showSettings: () => _mainWindow?.ShowSettingsFromMenu(),
                canShowAppDialogs: () => _mainWindow is { IsActive: true });

            // An emoji chosen in the macOS picker arrives while the window is in the background; macOS only.
            BackgroundTextInput.Install();

            if (StartupFailureMessage is { } startupFailure)
            {
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    I18n.Message.Of("startup.failedTitle"),
                    startupFailure);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // Loads effective settings before the window without creating or rewriting config.json.
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
            catch (UnreadableStoreException unreadable)
            {
                // The file is left exactly where it is, on this launch and every later one until the
                // user repairs or moves it. For the path list that is the point: opening with an empty
                // list would look exactly like losing it, and the first add would then write a fresh
                // file containing only that entry. Any other store got here because it could not be
                // set aside, and a reset would overwrite it.
                var pathList = unreadable.Label == QuarantineJournal.PathListLabel;
                Log.Warn("startup: a file could not be read; halting with it left in place",
                    new { label = unreadable.Label, path = unreadable.Path });
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    I18n.Message.Of(pathList ? "startup.pathListTitle" : "startup.failedTitle"),
                    pathList
                        ? FailurePresentation.PathListStartup(unreadable)
                        : FailurePresentation.StartupUnreadable(unreadable));
                RegisterOwnerActivation(desktop.MainWindow);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            catch (NewerFormatException newer)
            {
                // Intact data a newer PathHide wrote: opening without it would let this version write
                // over it, so startup stops with the file left exactly as it is.
                Log.Warn("startup: a file was written by a newer version; halting", new { path = newer.Path });
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    I18n.Message.Of("startup.failedTitle"),
                    FailurePresentation.NewerStore(newer));
                RegisterOwnerActivation(desktop.MainWindow);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            catch (Exception ex)
            {
                Log.Error("startup: failed", ex);
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    I18n.Message.Of("startup.failedTitle"),
                    FailurePresentation.Startup());
                RegisterOwnerActivation(desktop.MainWindow);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // Removes temp files an earlier session had to leave behind a still-running elevated child;
            // off the UI thread, and only files whose owning process has exited.
            if (OperatingSystem.IsWindows())
                _ = Task.Run(() => ElevatedApplyFiles.SweepLeftovers(System.IO.Path.GetTempPath()));

            // Before the main window exists, so its first frame and title bar take the saved theme.
            AppTheme.Apply(viewModel.Theme);
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            mainWindow.RestoreWindowGeometry();
            desktop.MainWindow = mainWindow;
            _mainWindow = mainWindow;
            RegisterOwnerActivation(mainWindow);

            // Report material recovery once the main window can own the dialog.
            mainWindow.Opened += async (_, _) =>
            {
                foreach (var quarantined in Storage.QuarantineJournal.Drain())
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
            WindowActivation.BringBack(window)));
    }

    /// <summary>
    /// Composition root: builds persistence, the OS-appropriate visibility service,
    /// and the view model. Settings are loaded here because the Windows service closes
    /// over the loaded instance to read the current hide mode, and window state because the
    /// window is placed before it is shown; path entries are loaded later, when the window
    /// calls <see cref="MainWindowViewModel.Initialize"/>.
    /// </summary>
    internal static MainWindowViewModel CreateMainViewModel()
    {
        var pathListStore = new PathListStore();
        var settingsStore = new SettingsStore();
        // Settings are re-derivable, so an unreadable config.json correctly falls back to
        // defaults; the recovery notice tells the user it happened. The path list does NOT —
        // see LoadPersistedState.
        var settings = settingsStore.Load().Value;

        // Window geometry remains independent, disposable, and outside backup history.
        var stateStore = new JsonStore<AppState>(
            AppState.FileName, QuarantineJournal.StateLabel, FormatVersions.State, recordBackup: false);
        var state = stateStore.Load().Value;

        // Key effective configuration at startup (the conventions' baseline): every user-tunable
        // setting, not a subset. Logging only the hide mode meant a session log could not answer
        // which UI font was in effect — the one setting that plausibly explains a rendering
        // complaint.
        Log.Info("config", new
        {
            language = settings.Language,
            theme = settings.Theme,
            hideMode = settings.WindowsHideMode,
            uiFontFamily = settings.UiFontFamily,
        });

        IVisibilityService visibilityService = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsVisibilityService(() => settings.WindowsHideMode)
            : new MacVisibilityService();

        // Only Windows has an elevation step for access-denied paths.
        IElevatedApplicator? elevatedApplicator = OperatingSystem.IsWindows()
            ? new ElevatedApplicator(WindowsElevatedChild.LaunchAsync, System.IO.Path.GetTempPath(), StorageRoot.Directory)
            : null;

        return new MainWindowViewModel(new BoundedVisibility(visibilityService), pathListStore, settingsStore, settings, stateStore, state)
        {
            ComputerLanguages = ComputerLanguages,
            ElevatedApplicator = elevatedApplicator,
        };
    }
}
