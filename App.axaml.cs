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
        var settingsStore = new JsonStore<AppSettings>("settings.json", "settings");
        var settings = settingsStore.Load();

        IVisibilityService visibilityService = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsVisibilityService(() => settings.WindowsHideMode)
            : new MacVisibilityService();

        return new MainWindowViewModel(visibilityService, pathListStore, settingsStore, settings);
    }
}
