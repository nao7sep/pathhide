using System.Collections.Generic;
using System.Threading.Tasks;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;

namespace PathHide.Tests.I18n;

/// <summary>
/// A main window with something on every surface that holds words: rows in every state the list can
/// show, the status bar counting them, and both result strips open. The language gates read what it
/// draws, so a surface that is empty in a test is one they cannot see.
/// </summary>
internal static class PopulatedMainWindow
{
    internal static (MainWindow Window, MainWindowViewModel ViewModel) Create()
    {
        var visibility = new FakeVisibilityService();
        visibility.Set("/hidden-file", ActualState.Hidden, ItemKind.File);
        visibility.Set("/visible-directory", ActualState.Visible, ItemKind.Directory);
        visibility.Set("/missing", ActualState.Missing, ItemKind.Unknown);
        visibility.Set("/denied-link", ActualState.AccessDenied, ItemKind.Symlink);
        visibility.Set("/broken-other", ActualState.Error, ItemKind.Other);
        visibility.Set("/stalled-share", ActualState.Unresponsive, ItemKind.Unknown);

        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value =
            [
                new PathEntry { Path = "/hidden-file", DesiredVisibility = DesiredVisibility.Hidden },
                new PathEntry { Path = "/visible-directory", DesiredVisibility = DesiredVisibility.Shown },
                new PathEntry { Path = "/missing", DesiredVisibility = DesiredVisibility.Hidden },
                new PathEntry { Path = "/denied-link", DesiredVisibility = DesiredVisibility.Hidden },
                new PathEntry { Path = "/broken-other", DesiredVisibility = DesiredVisibility.Shown },
                new PathEntry { Path = "/stalled-share", DesiredVisibility = DesiredVisibility.Hidden },
                new PathEntry { Path = @"C:\windows-path", DesiredVisibility = DesiredVisibility.Hidden },
                new PathEntry { Path = @"\\server\share", DesiredVisibility = DesiredVisibility.Hidden },
            ],
        };
        var settings = new FakeJsonStore<AppSettings>();
        var viewModel = new MainWindowViewModel(new BoundedVisibility(visibility), paths, settings, settings.Load().Value, new FakeJsonStore<AppState>(), new AppState());
        return (new MainWindow { DataContext = viewModel }, viewModel);
    }

    /// <summary>A main window with an empty list, and the view model the app always gives it.</summary>
    internal static MainWindow Empty()
    {
        var settings = new FakeJsonStore<AppSettings>();
        var viewModel = new MainWindowViewModel(
            new BoundedVisibility(new FakeVisibilityService()), new FakeJsonStore<List<PathEntry>>(), settings, settings.Load().Value, new FakeJsonStore<AppState>(), new AppState());
        return new MainWindow { DataContext = viewModel };
    }

    /// <summary>Waits for the scan the window started, then puts both result strips on screen.</summary>
    internal static async Task SettleAsync(MainWindowViewModel viewModel)
    {
        await viewModel.ScanTask;
        viewModel.ReportLogRevealFailure();
        await viewModel.AddDroppedPathsAsync(["/hidden-file"], unavailable: 1);
    }
}
