using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.I18n;

/// <summary>
/// A label fits its control in every language (localization conventions).
///
/// Buttons and menus size to their words, so a longer language makes them wider rather than cutting
/// them off. What does not grow is anything given a fixed width: the dialogs, and the path list's
/// columns. So these open each of those in all ten languages and measure the text against the room
/// it was given, with the real font and the real layout rather than by eye — and check that the
/// toolbar, which never wraps, still fits the window's default width.
/// </summary>
public class LabelFitTests : WindowTest
{
    // A label may exceed its box by this much before it is called clipped: Skia's measurement and
    // Avalonia's arrangement round differently, and a fraction of a pixel is not a defect.
    private const double Tolerance = 1.0;

    // The main window's designed default width (MainWindow.axaml).
    private const double DefaultWindowWidth = 1280;

    public static TheoryData<string> Tags() => new() { "en", "de", "es", "fr", "it", "pt-BR", "ru", "ja", "ko", "zh-Hans" };

    [AvaloniaTheory]
    [MemberData(nameof(Tags))]
    public void the_settings_dialog_clips_nothing(string tag)
    {
        using var speaking = Localizer.Speaking(tag);

        var dialog = Show(new SettingsDialog(
            Languages.System, AppSettings.DefaultUiFontFamily, ThemePreference.System,
            isHiddenAndSystem: false, showWindowsHideMode: true, (_, _, _, _) => Task.FromResult<Message?>(null)));

        // Section headers, the three theme choices, the checkbox and the two buttons.
        AssertNothingClipped(dialog, tag, atLeast: 10);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Tags))]
    public void the_shortcuts_dialog_clips_nothing(string tag)
    {
        using var speaking = Localizer.Speaking(tag);

        var owner = Show(new Window());
        var dialog = Show(new ShortcutsDialog(ShortcutCatalog.Build(owner)));

        // Section headers and key legends; the descriptions wrap by design.
        AssertNothingClipped(dialog, tag, atLeast: 10);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Tags))]
    public void the_about_dialog_clips_nothing(string tag)
    {
        using var speaking = Localizer.Speaking(tag);

        var dialog = Show(new AboutDialog(_ => true));

        // The About dialog's body and licence wrap by design, and its link buttons carry their words as
        // inline runs; the name, the version and the Close button are the labels that cannot move.
        AssertNothingClipped(dialog, tag, atLeast: 3);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Tags))]
    public async Task the_main_window_clips_nothing_at_its_default_size(string tag)
    {
        using var speaking = Localizer.Speaking(tag);

        var (window, viewModel) = PopulatedMainWindow.Create();
        Show(window);
        await PopulatedMainWindow.SettleAsync(viewModel);
        Dispatcher.UIThread.RunJobs();

        // Column headers, every state a column can show, the toolbar's buttons and the status bar.
        AssertNothingClipped(window, tag, atLeast: 30);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Tags))]
    public void the_toolbar_fits_the_windows_default_width(string tag)
    {
        using var speaking = Localizer.Speaking(tag);

        var window = Show(PopulatedMainWindow.Empty());
        var toolbar = window.FindControl<Control>("Toolbar")!;

        // At its widest: Cancel only appears while a scan runs, and a scan runs at every launch.
        window.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, Localizer.T("common.cancel"))).IsVisible = true;
        // Every level re-measured: a panel whose own measure is still valid answers with the size it
        // had before Cancel appeared.
        foreach (var control in toolbar.GetVisualDescendants().OfType<Avalonia.Layout.Layoutable>().Prepend(toolbar))
            control.InvalidateMeasure();
        toolbar.Measure(Size.Infinity);

        // The toolbar never wraps and sets the window's minimum width, so a language whose toolbar is
        // wider than the default would open the window wider than designed, and on a screen narrower
        // than the toolbar would cut its last buttons off. 1280 logical pixels is also a common
        // Windows laptop's whole screen: 1920 by 1080 at 150 per cent.
        Assert.True(
            toolbar.DesiredSize.Width <= DefaultWindowWidth,
            $"{tag}: the toolbar needs {toolbar.DesiredSize.Width:0} px, wider than the {DefaultWindowWidth:0} px default window.");
    }

    private static void AssertNothingClipped(Visual root, string tag, int atLeast)
    {
        var clipped = new List<string>();
        var measured = 0;

        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            // Text that wraps or is deliberately trimmed is not clipped; neither is a label that was
            // never given a size, which happens to anything not currently on screen.
            if (text.Text is not { Length: > 0 } words)
                continue;
            if (text.TextWrapping != TextWrapping.NoWrap || text.TextTrimming != TextTrimming.None)
                continue;
            if (text.Bounds.Width <= 0)
                continue;

            measured++;
            var needed = Measure(words, text);
            if (needed > text.Bounds.Width + Tolerance)
                clipped.Add($"“{words}” needs {needed:F0}px in {text.Bounds.Width:F0}px");
        }

        // A gate that measures nothing would pass forever: if a redesign makes every label wrap or
        // trim, this says so rather than quietly stopping work.
        Assert.True(
            measured >= atLeast,
            $"{tag}: only {measured} labels were measured, fewer than the {atLeast} expected; the check is not looking at anything.");
        Assert.True(clipped.Count == 0, $"{tag}: {string.Join("; ", clipped)}");
    }

    private static double Measure(string words, TextBlock text) =>
        new FormattedText(
            words,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight),
            text.FontSize,
            null).Width;
}
