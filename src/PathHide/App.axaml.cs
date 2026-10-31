using System;
using System.Threading.Tasks;
using System.Collections.Generic;
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
        Name = AppIdentity.Name;
        MenuGestureColumn.Install();
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The app quits with its main window. The records window is a window of its own, which
            // must neither keep a closed main window's app running nor be what is left of it.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

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
                AppIdentity.Name,
                showAbout: () => _mainWindow?.ShowAboutFromMenu(),
                showSettings: () => _mainWindow?.ShowSettingsFromMenu(),
                canShowAppDialogs: () => _mainWindow is { IsActive: true });

            // An emoji chosen in the macOS picker arrives while the window is in the background; macOS only.
            BackgroundTextInput.Install();

            if (StartupFailureMessage is { } startupFailure)
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    I18n.Message.Of("startup.failedTitle"),
                    startupFailure);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // Loads effective settings before the window without creating or rewriting config.json.
            // Backup history is handed each managed save's bytes once its atomic rename lands (see
            // JsonStore/BackupStore), so there is no startup backup pass to kick off here.
            //
            // A refused file stops startup before defaults could overwrite it (ReadStartupStores).
            MainWindowViewModel viewModel;
            try
            {
                // Required reads settle, off the UI thread, before the normal shell exists.
                base.OnFrameworkInitializationCompleted();
                var loaded = await Task.Run(() => ReadStartupStores(RecordsOpenFailure));
                viewModel = CreateMainViewModel(loaded);
                await viewModel.ValidateLoadedPathsAsync();
            }
            catch (StartupRefusedException refused)
            {
                // Every refused file is left exactly where it is, on this launch and every later one
                // until the user uses the newer version, or repairs, restores or moves it: opening
                // without it could write over it, or look exactly like losing it.
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    I18n.Message.Of("startup.failedTitle"),
                    FailurePresentation.StartupRefused(refused.Refusals));
                RegisterOwnerActivation(desktop.MainWindow);
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow.Show();
                return;
            }
            catch (Exception ex)
            {
                Log.Error("startup: failed", ex);
                desktop.MainWindow = NoticeDialog.CreateStartupFailure(
                    I18n.Message.Of("startup.failedTitle"),
                    FailurePresentation.Startup());
                RegisterOwnerActivation(desktop.MainWindow);
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow.Show();
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
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            _mainWindow = mainWindow;
            RegisterOwnerActivation(mainWindow);
            mainWindow.Show();
            return;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void RegisterOwnerActivation(Window window)
    {
        SingleInstanceLease.RegisterOwnerActivationHandler(() => Dispatcher.UIThread.Post(() =>
            WindowActivation.BringBack(window)));
    }

    /// <summary>
    /// Why the records database could not be opened, from <c>Program</c>, which opens it before the app
    /// is built; null when it opened.
    /// </summary>
    internal static Exception? RecordsOpenFailure { get; set; }

    /// <summary>
    /// Prepare every persisted value before the normal shell exists, refusing launch for every file it
    /// must not start with, all of them named at once (store-recovery-conventions, What a refused file
    /// does): any newer file, and an unreadable path list or settings file. Unreadable window state takes
    /// its defaults; an unreadable records or backup database is left untouched and switched off for the
    /// session, with a notice. Nothing is written here but the removal of an interrupted save's scratch.
    /// </summary>
    /// <exception cref="StartupRefusedException">Launch must stop; every refused file is named.</exception>
    internal static StartupStores ReadStartupStores(Exception? recordsFailure = null)
    {
        var refusals = new List<StartupRefusal>();
        T Read<T>(Func<T> load, T refused)
        {
            try { return load(); }
            catch (Exception ex) when (StartupRefusal.From(ex) is { } refusal)
            {
                Log.Warn("startup: refused a file, left as it is", new { path = refusal.Path, reason = refusal.Reason });
                refusals.Add(refusal);
                return refused;
            }
        }

        var settings = Read(() => new SettingsStore().Load().Value, new AppSettings());
        var state = Read(() => CreateStateStore().Load().Value, new AppState());
        var paths = Read(() => new PathListStore().Load().Value, []);

        I18n.Message? recordsOff = null;
        if (recordsFailure is NewerFormatException newerRecords)
            refusals.Add(StartupRefusal.From(newerRecords)!);
        else if (recordsFailure is not null)
            recordsOff = FailurePresentation.RecordsOff();

        // Opened lazily at the first save, so its header is checked here, without writing to it.
        I18n.Message? backupsOff = null;
        var backups = System.IO.Path.Combine(StorageRoot.Directory, Backup.BackupStore.FileName);
        try
        {
            FileFormats.CheckDatabase(backups, FileFormats.Backups);
        }
        catch (NewerFormatException newer)
        {
            refusals.Add(StartupRefusal.From(newer)!);
        }
        catch (Exception ex)
        {
            Log.Warn("startup: the backup history could not be read; off for this session, left as it is", ex, new { path = backups });
            Backup.BackupStore.Disable();
            backupsOff = FailurePresentation.BackupsOff();
        }

        if (refusals.Count > 0)
            throw new StartupRefusedException(refusals);

        // Only a launch that goes ahead may clean up after an interrupted save.
        if (System.IO.Directory.Exists(StorageRoot.Directory))
            StoreScratch.Sweep(StorageRoot.Directory);
        return new StartupStores(settings, state, paths, recordsOff, backupsOff);
    }

    /// <summary>
    /// Window geometry: independent, disposable presentation state outside backup history. An unusable
    /// file gives defaults silently and the next save replaces it.
    /// </summary>
    private static JsonStore<AppState> CreateStateStore() =>
        new(AppState.FileName, "state", FileFormats.State, recordBackup: false);

    /// <summary>Compose the platform services and view model using only prepared persisted values.</summary>
    internal static MainWindowViewModel CreateMainViewModel(StartupStores loaded)
    {
        var pathListStore = new PathListStore();
        var settingsStore = new SettingsStore();
        // ReadStartupStores succeeded, so settings and the path list are what the files hold.
        var settings = loaded.Settings;

        var stateStore = CreateStateStore();
        var state = loaded.State;

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
            displayTimeZone = settings.DisplayTimeZone,
        });

        var (visibilityService, elevatedApplicator) =
            CreatePlatformServices(OperatingSystem.IsWindows());

        var viewModel = new MainWindowViewModel(new VisibilityCalls(visibilityService), pathListStore, settingsStore, settings, stateStore, state, loaded.Paths)
        {
            ComputerLanguages = ComputerLanguages,
            ElevatedApplicator = elevatedApplicator,
        };
        viewModel.ShowSideStoreNotices(loaded.RecordsOff, loaded.BackupsOff);
        return viewModel;
    }

    /// <summary>
    /// The file-visibility writer for this system, and the elevation step for access-denied paths, which
    /// only Windows has.
    /// </summary>
    internal static (IVisibilityService Visibility, IElevatedApplicator? Elevated) CreatePlatformServices(
        bool windows) =>
        windows
            ? (new WindowsVisibilityService(),
                new ElevatedApplicator(LaunchElevatedAsync, System.IO.Path.GetTempPath(), StorageRoot.Directory))
            : (new MacVisibilityService(), null);

    private static Task<Task<int>?> LaunchElevatedAsync(IReadOnlyList<string> arguments) =>
        OperatingSystem.IsWindows() ? WindowsElevatedChild.LaunchAsync(arguments) : Task.FromResult<Task<int>?>(null);
}

/// <summary>What launch read: the values to start with, and the notices for side stores switched off.</summary>
internal sealed record StartupStores(
    AppSettings Settings,
    AppState State,
    List<PathEntry> Paths,
    I18n.Message? RecordsOff = null,
    I18n.Message? BackupsOff = null);
