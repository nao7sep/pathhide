using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.Models;
using PathHide.Services;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class PathColumnTests : WindowTest
{
    private const string ShortPath = "/short";

    // One unbroken file name longer than the column, after directories: the end, which names the
    // file, is what a clip would hide.
    private const string LongPath =
        "/Users/someone/Documents/Projects/Archive/2026/Quarterly reports and supporting material/" +
        "a-single-file-name-long-enough-that-it-has-no-break-opportunity-inside-the-column.txt";

    [AvaloniaFact]
    public async Task A_long_path_wraps_so_the_whole_value_stays_visible()
    {
        var visibility = new FakeVisibilityService();
        visibility.Set(ShortPath, ActualState.Hidden, ItemKind.File);
        visibility.Set(LongPath, ActualState.Hidden, ItemKind.File);
        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value =
            [
                new PathEntry { Path = ShortPath, DesiredVisibility = DesiredVisibility.Hidden },
                new PathEntry { Path = LongPath, DesiredVisibility = DesiredVisibility.Hidden },
            ],
        };
        var settings = new FakeSettingsStore();
        var viewModel = new MainWindowViewModel(new BoundedVisibility(visibility), paths, settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), paths.Load().Value);
        var window = Show(new MainWindow { DataContext = viewModel, Width = 900, Height = 500 });
        await viewModel.ScanTask;
        Dispatcher.UIThread.RunJobs();

        var longText = PathText(window, LongPath);
        var shortText = PathText(window, ShortPath);

        Assert.Equal(TextWrapping.Wrap, longText.TextWrapping);
        Assert.Equal(TextTrimming.None, longText.TextTrimming);
        // Every line fits the cell: nothing runs past the column edge to be clipped.
        Assert.True(longText.TextLayout.TextLines.Count > 1, "The long path stayed on one line.");
        Assert.All(longText.TextLayout.TextLines, line => Assert.True(line.WidthIncludingTrailingWhitespace <= longText.Bounds.Width + 0.5));
        // The row grows to hold every line instead of cutting the lower ones off.
        var longRow = longText.FindAncestorOfType<DataGridRow>()!;
        var shortRow = shortText.FindAncestorOfType<DataGridRow>()!;
        Assert.True(longRow.Bounds.Height > shortRow.Bounds.Height);
        Assert.True(longText.Bounds.Height >= longText.TextLayout.Height - 0.5);
    }

    // A hand-edited list can hold any string. Avalonia lays out one huge line for seconds or minutes,
    // so the list shows a shortened copy with one marker while the entry keeps the whole path.
    [AvaloniaFact]
    public async Task A_path_too_long_to_lay_out_shows_a_marked_copy_and_keeps_its_whole_value()
    {
        var huge = @"C:" + string.Concat(Enumerable.Repeat(@"\segment", 6_000));
        var visibility = new FakeVisibilityService();
        visibility.Set(huge, ActualState.Missing, ItemKind.Unknown);
        var paths = new FakeJsonStore<List<PathEntry>>
        {
            Value = [new PathEntry { Path = huge, DesiredVisibility = DesiredVisibility.Hidden }],
        };
        var settings = new FakeSettingsStore();
        var viewModel = new MainWindowViewModel(new BoundedVisibility(visibility), paths, settings,
            settings.Load().Value, new FakeJsonStore<AppState>(), new AppState(), paths.Load().Value);
        var window = Show(new MainWindow { DataContext = viewModel, Width = 900, Height = 500 });
        await viewModel.ScanTask;
        Dispatcher.UIThread.RunJobs();

        var row = viewModel.Rows.Single();
        PathText(window, row.DisplayPath);
        Assert.Equal(huge, row.Path);
        Assert.Equal(huge, row.Entry.Path);
        Assert.EndsWith("…", row.DisplayPath);
        Assert.Equal(PathHide.Text.DisplayText.LineLimit + 1, row.DisplayPath.Length);
        Assert.StartsWith(row.DisplayPath[..^1], huge);
    }

    private static TextBlock PathText(Window window, string path) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == path);
}
