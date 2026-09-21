using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.I18n;
using PathHide.Models;
using PathHide.Tests.Fakes;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.I18n;

/// <summary>
/// What happens when the language changes while the app is open.
///
/// This is the whole point of holding keys rather than words: a window that is already on screen
/// speaks the new language without being rebuilt, and nothing has to be restarted. A control assigned
/// once in a constructor — which is every dialog here — must follow too.
/// </summary>
public class LanguageChangeTests : WindowTest
{
    [AvaloniaFact]
    public async Task the_main_window_follows_the_language_everywhere_it_shows_words()
    {
        var (window, viewModel) = PopulatedMainWindow.Create();
        Show(window);
        await PopulatedMainWindow.SettleAsync(viewModel);
        Dispatcher.UIThread.RunJobs();

        var reload = Button(window, English.Of("toolbar.reload"));
        var header = ColumnHeader(window, English.Of("grid.actual"));
        var denied = viewModel.Rows.Single(row => row.ActualState == ActualState.AccessDenied);
        var strip = viewModel.OperationalResults.Single();
        Assert.Equal(English.Of("actual.accessDenied"), denied.ActualStateText);

        using (Localizer.Speaking("ja"))
        {
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Localizer.T("toolbar.reload"), reload.Content);
            Assert.Equal(Localizer.T("grid.actual"), header.Content);
            Assert.Equal(Localizer.T("actual.accessDenied"), denied.ActualStateText);
            Assert.Equal(Localizer.T("actual.accessDenied"), CellTexts(window).First(text => text == denied.ActualStateText));
            Assert.Equal(Localizer.Of(viewModel.Summary()), viewModel.StatusBarText);
            Assert.Equal(Localizer.Of(strip.Message), strip.Text);
            Assert.Equal(Localizer.Of(viewModel.PathAddResult!.Message), viewModel.PathAddResult.Text);
            Assert.NotEqual(English.Of("toolbar.reload"), reload.Content);
            Assert.NotEqual(English.Of(viewModel.Summary()), viewModel.StatusBarText);
        }

        // And back, so the change is a re-reading rather than a one-way overwrite.
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(English.Of("toolbar.reload"), reload.Content);
        Assert.Equal(English.Of("grid.actual"), header.Content);
        Assert.Equal(English.Of("actual.accessDenied"), denied.ActualStateText);
    }

    [AvaloniaFact]
    public void the_main_windows_minimum_width_follows_its_toolbar()
    {
        // The toolbar never wraps and the window's minimum width is the toolbar's, so new labels must
        // move the minimum or the window could be dragged narrower than its own buttons.
        var window = Show(PopulatedMainWindow.Empty());
        var layout = window.FindControl<Control>("LayoutRoot")!;
        var toolbar = window.FindControl<Control>("Toolbar")!;

        using (Localizer.Speaking("de"))
        {
            Dispatcher.UIThread.RunJobs();
            toolbar.Measure(Avalonia.Size.Infinity);

            Assert.True(
                layout.MinWidth >= toolbar.DesiredSize.Width,
                $"The minimum is {layout.MinWidth:0} px for a {toolbar.DesiredSize.Width:0} px toolbar.");
        }
    }

    [AvaloniaFact]
    public void a_closed_main_window_stops_listening()
    {
        var window = Show(PopulatedMainWindow.Empty());
        var header = ColumnHeader(window, English.Of("grid.path"));
        window.Close();
        Dispatcher.UIThread.RunJobs();

        using (Localizer.Speaking("ru"))
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(English.Of("grid.path"), header.Content);
        }
    }

    [AvaloniaFact]
    public void a_dialog_built_in_code_follows_the_language()
    {
        var dialog = Show(new SettingsDialog(
            Languages.System, AppSettings.DefaultUiFontFamily, ThemePreference.System,
            isHiddenAndSystem: false, showWindowsHideMode: true, (_, _, _, _) => null));

        var theme = dialog.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == English.Of("settings.theme"));
        var save = Button(dialog, English.Of("common.save"));

        using (Localizer.Speaking("ru"))
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Localizer.T("settings.theme"), theme.Text);
            Assert.Equal(Localizer.T("common.save"), save.Content);
            Assert.Equal(Localizer.T("settings.title"), dialog.Title);
        }
    }

    [AvaloniaFact]
    public void a_window_that_has_closed_is_left_alone_and_corrected_if_it_opens_again()
    {
        var block = new TextBlock();
        Localized.SetText(block, "common.close");
        var window = Show(new Window { Content = block });
        Assert.Equal(English.Of("common.close"), block.Text);

        window.Close();
        window.Content = null;
        Dispatcher.UIThread.RunJobs();

        // Closed is not collected: the control is still in the retranslation table, and writing into
        // it now would reach template bindings and glyph runs that went with its window.
        using (Localizer.Speaking("ja"))
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(English.Of("common.close"), block.Text);

            // Shown again, it catches up with the language it slept through.
            Show(new Window { Content = block });
            Assert.Equal(Localizer.T("common.close"), block.Text);
        }
    }

    [AvaloniaFact]
    public void the_language_list_names_every_language_in_its_own_words()
    {
        var options = LanguageOption.All();

        // System first, then the ten languages.
        Assert.Equal(Languages.System, options[0].Value);
        Assert.Equal(English.Of("settings.languageSystem"), options[0].Name);
        Assert.Equal(Languages.Tags, options.Skip(1).Select(option => option.Value));

        // Each name is the language's own, not a translation of it.
        Assert.Contains(options, option => option.Name == "日本語");
        Assert.Contains(options, option => option.Name == "Русский");
        Assert.Contains(options, option => option.Name == "中文");
        Assert.Contains(options, option => option.Name == "Português");
    }

    [AvaloniaFact]
    public void choosing_a_language_enables_save()
    {
        var dialog = Show(new SettingsDialog(
            Languages.System, AppSettings.DefaultUiFontFamily, ThemePreference.System,
            isHiddenAndSystem: false, showWindowsHideMode: false, (_, _, _, _) => null));
        var save = Button(dialog, English.Of("common.save"));
        var languages = dialog.GetVisualDescendants().OfType<ComboBox>().Single();
        Assert.False(save.IsEnabled);

        languages.SelectedItem = languages.Items.OfType<LanguageOption>().Single(option => option.Value == "fr");

        Assert.True(save.IsEnabled);
        Assert.Equal("fr", dialog.SelectedLanguage);
    }

    [AvaloniaFact]
    public void a_saved_language_is_spoken_at_once_and_stored()
    {
        var settings = new FakeJsonStore<AppSettings>();
        var viewModel = new MainWindowViewModel(
            new FakeVisibilityService(), new FakeJsonStore<System.Collections.Generic.List<PathEntry>>(),
            settings, settings.Load().Value);
        // Speaking the current language is only the guard: it puts the language and the preference
        // back exactly when the test ends, whatever the save did to them.
        using var restore = Localizer.Speaking(Localizer.Language);

        Assert.Null(viewModel.TryApplySettings("de", AppSettings.DefaultUiFontFamily, false, ThemePreference.System));

        Assert.Equal("de", Localizer.Language);
        Assert.Equal("de", settings.LastSaved!.Language);
        Assert.Equal("de", viewModel.Language);
    }

    [AvaloniaFact]
    public void a_binding_re_reads_when_a_view_model_says_every_property_changed()
    {
        // The view model answers a language change with one PropertyChanged carrying no name, which
        // means "all of them". Everything the main window shows depends on Avalonia honouring that, so
        // it is pinned here rather than assumed.
        var source = new EverythingChanged();
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, new Binding(nameof(EverythingChanged.Words)) { Source = source });
        Show(new Window { Content = text });
        Assert.Equal("before", text.Text);

        source.Words = "after";
        source.Raise();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("after", text.Text);
    }

    private sealed class EverythingChanged : INotifyPropertyChanged
    {
        public string Words { get; set; } = "before";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static Button Button(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, content));

    private static ContentControl ColumnHeader(Window window, string content) =>
        window.GetVisualDescendants().OfType<ContentControl>()
            .Single(control => control.GetType().Name == "DataGridColumnHeader" && Equals(control.Content, content));

    private static string[] CellTexts(Window window) =>
        window.GetVisualDescendants().OfType<DataGridCell>()
            .SelectMany(cell => cell.GetVisualDescendants().OfType<TextBlock>())
            .Select(text => text.Text ?? "")
            .ToArray();
}
