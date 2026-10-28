using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// A path identifies its row, so it takes one line and, past its column, keeps its final name and
/// loses its middle (interface-styling conventions, Labels); its tooltip shows it whole.
/// </summary>
public sealed class PathColumnTests : WindowTest
{
    private const string ShortPath = "/short";
    private const string FinalName = "report-final.txt";

    [AvaloniaTheory]
    [InlineData("/Users/someone/Documents/Projects/Archive/2026/Quarterly reports and supporting material/drafts/" + FinalName)]
    [InlineData(@"C:\Users\someone\Documents\Projects\Archive\2026\Quarterly reports and supporting material\drafts\" + FinalName)]
    public async Task A_long_path_takes_one_line_and_keeps_its_final_name(string longPath)
    {
        var visibility = new FakeVisibilityService();
        visibility.Set(ShortPath, ActualState.Hidden, ItemKind.File);
        visibility.Set(longPath, ActualState.Hidden, ItemKind.File);
        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value =
            [
                new PathEntry { Path = ShortPath, DesiredVisibility = DesiredVisibility.Hidden },
                new PathEntry { Path = longPath, DesiredVisibility = DesiredVisibility.Hidden },
            ],
        };
        var settings = new FakeSettingsStore();
        var viewModel = new MainWindowViewModel(new VisibilityCalls(visibility), paths, settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), paths.Load().Value);
        var window = Show(new MainWindow { DataContext = viewModel, Width = 900, Height = 500 });
        await viewModel.ScanTask;
        Dispatcher.UIThread.RunJobs();

        var longText = PathText(window, longPath);
        var shortText = PathText(window, ShortPath);

        Assert.Equal(TextWrapping.NoWrap, longText.TextWrapping);
        var line = Assert.Single(longText.TextLayout.TextLines);
        Assert.True(line.HasCollapsed, "The long path was not shortened.");
        var shown = string.Concat(line.TextRuns.OfType<ShapedTextRun>().Select(run => run.Text.ToString()));
        Assert.EndsWith(FinalName, shown);
        Assert.StartsWith(longPath[..3], shown);
        Assert.Contains("…", shown);
        // Rows keep one height, and the tooltip carries the whole path.
        var longRow = longText.FindAncestorOfType<DataGridRow>()!;
        var shortRow = shortText.FindAncestorOfType<DataGridRow>()!;
        Assert.Equal(shortRow.Bounds.Height, longRow.Bounds.Height);
        Assert.Equal(longPath, ToolTip.GetTip(longText.FindAncestorOfType<DataGridCell>()!));
        Assert.False(Assert.Single(shortText.TextLayout.TextLines).HasCollapsed);
    }

    private static TextBlock PathText(Window window, string path) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == path);
}
