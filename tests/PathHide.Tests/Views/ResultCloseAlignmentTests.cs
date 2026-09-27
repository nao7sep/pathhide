using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.I18n;
using PathHide.Tests.I18n;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

/// <summary>
/// A result's close mark centres on the first line of its words, whether they fill one line or
/// wrap, and whatever font the words fall back to (Japanese falls back from Inter to a taller font).
/// Each case changes the words of a result already on screen, so it also checks that the mark follows
/// a change of text.
/// </summary>
public class ResultCloseAlignmentTests : WindowTest
{
    private const string Latin = "The path list could not be saved because the disk is full or the folder is read-only. ";
    private const string Japanese = "パスの一覧を保存できませんでした。ディスクがいっぱいか、フォルダーが読み取り専用です。";

    [AvaloniaTheory]
    [InlineData(Latin, 1)]
    [InlineData(Latin, 12)]
    [InlineData(Japanese, 1)]
    [InlineData(Japanese, 12)]
    public async Task the_main_windows_results_centre_their_close_mark_on_the_first_line(string words, int repeat)
    {
        var (window, viewModel) = PopulatedMainWindow.Create();
        Show(window);
        await PopulatedMainWindow.SettleAsync(viewModel);
        Dispatcher.UIThread.RunJobs();

        var results = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("pathResult")).ToList();
        Assert.Equal(2, results.Count);
        foreach (var result in results)
            AssertCentredOnFirstLine(window, result, string.Concat(Enumerable.Repeat(words, repeat)), wraps: repeat > 1);
    }

    [AvaloniaTheory]
    [InlineData("The page could not be opened.", 1)]
    [InlineData(Japanese, 6)]
    public void the_about_dialogs_result_centres_its_close_mark_on_the_first_line(string words, int repeat)
    {
        var about = Show(new AboutDialog(_ => true));
        var result = about.GetVisualDescendants().OfType<Border>()
            .First(border => border.Child is Grid grid && grid.Children.OfType<Button>().Any(button => button.Classes.Contains("resultClose")));
        result.IsVisible = true;

        AssertCentredOnFirstLine(about, result, string.Concat(Enumerable.Repeat(words, repeat)), wraps: repeat > 1);
    }

    private static void AssertCentredOnFirstLine(Window window, Border result, string words, bool wraps)
    {
        var text = result.GetVisualDescendants().OfType<TextBlock>().Single(block => block.TextWrapping == TextWrapping.Wrap);
        var close = result.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("resultClose"));
        text.Text = words;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var lines = text.TextLayout.TextLines;
        Assert.Equal(wraps, lines.Count > 1);
        var firstLineCentre = text.TranslatePoint(default, window)!.Value.Y + lines[0].Height / 2;
        var closeCentre = close.TranslatePoint(default, window)!.Value.Y + close.Bounds.Height / 2;
        Assert.True(
            Math.Abs(closeCentre - firstLineCentre) <= 1,
            $"the close mark's centre is {closeCentre - firstLineCentre:F2}px from the first line's ({lines.Count} lines)");
        // Only the mark moves: it keeps its own size, and its layout box stays as tall as its style makes it.
        Assert.Equal(22, close.Bounds.Height);
        Assert.Equal(-6, close.Margin.Top + close.Margin.Bottom, 3);
    }
}
