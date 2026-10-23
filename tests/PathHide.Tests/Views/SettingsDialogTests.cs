using System.Threading.Tasks;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using PathHide.Models;
using PathHide.Services;
using PathHide.Storage;
using PathHide.Tests.Fakes;
using PathHide.Views;
using PathHide.I18n;
using PathHide.Tests.I18n;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class SettingsDialogTests
{
    [AvaloniaFact]
    public void FailedSaveShowsTheChosenFailureAndKeepsTheDraft()
    {
        // The save answers with what to tell the reader, never with diagnostics: a failure can only
        // be a catalogue message, so an exception's own text has no way into the dialog.
        var dialog = new SettingsDialog(
            Languages.System,
            "Inter",
            ThemePreference.System,
            isHiddenAndSystem: false,
            showWindowsHideMode: false,
            DisplayZone.System,
            (_, _, _, _, _) => Task.FromResult<Message?>(FailurePresentation.SettingsSave(new System.IO.IOException("Access to /private/test/config.tmp is denied."))));
        var font = dialog.GetLogicalDescendants().OfType<TextBox>().Single();
        font.Text = "Menlo";
        var save = dialog.GetLogicalDescendants().OfType<Button>()
            .Single(button => Equals(button.Tag, "save"));

        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(dialog.Accepted);
        Assert.Equal("Menlo", font.Text);
        var error = dialog.GetLogicalDescendants().OfType<TextBlock>().Single(block =>
            block.IsVisible && block.Text?.Contains("could not be saved") == true);
        Assert.Equal(English.Of("failure.settingsSave"), error.Text);
        Assert.DoesNotContain("/private/test", error.Text);

        // A growing result remains in the shell's scrollable body. The fixed
        // footer stays outside that region, so longer copy or a larger UI font
        // cannot compress the fields or push both actions out of reach.
        Assert.Equal(TextWrapping.Wrap, error.TextWrapping);
        Assert.NotEmpty(error.GetLogicalAncestors().OfType<ScrollViewer>());
        var footer = dialog.GetLogicalDescendants().OfType<StackPanel>()
            .Single(panel => panel.Name == "ButtonPanel");
        Assert.Empty(footer.GetLogicalAncestors().OfType<ScrollViewer>());
        Assert.All(footer.Children.OfType<Button>(), button => Assert.True(button.MinWidth >= 80));
    }

    // A font entry that matches no installed family says so under the field, and Save stays available:
    // the bundled font shows meanwhile, instead of the entry falling back silently.
    [AvaloniaFact]
    public void A_font_entry_with_no_installed_family_shows_a_note_and_still_saves()
    {
        var dialog = new SettingsDialog(Languages.System, "", ThemePreference.System,
            false, false, DisplayZone.System, (_, _, _, _, _) => Task.FromResult<Message?>(null));
        var font = dialog.GetLogicalDescendants().OfType<TextBox>().Single();
        var note = dialog.GetLogicalDescendants().OfType<TextBlock>().Single(block => block.Name == "UiFontMissing");
        var save = dialog.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Tag, "save"));
        Assert.False(note.IsVisible);

        font.Text = "No Such Font 99999, Also Missing";
        Dispatcher.UIThread.RunJobs();

        Assert.True(note.IsVisible);
        Assert.Equal(English.Of("settings.uiFontMissing"), note.Text);
        Assert.True(save.IsEnabled);

        font.Text = $"No Such Font 99999, {FontManager.Current.SystemFonts.First().Name}";
        Dispatcher.UIThread.RunJobs();
        Assert.False(note.IsVisible);
        font.Text = "";
        Dispatcher.UIThread.RunJobs();
        Assert.False(note.IsVisible);
    }

    [AvaloniaFact]
    public async Task SuccessfulSaveKeepsNewerDraftOpenAndAdvancesSavedBaseline()
    {
        var pending = new TaskCompletionSource<Message?>();
        var dialog = new SettingsDialog(Languages.System, "Inter", ThemePreference.System,
            false, false, DisplayZone.System, (_, _, _, _, _) => pending.Task);
        var font = dialog.GetLogicalDescendants().OfType<TextBox>().Single();
        var save = dialog.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Tag, "save"));
        font.Text = "Menlo";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        font.Text = "Arial";
        pending.SetResult(null);
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
        Assert.False(dialog.Accepted);
        Assert.Equal("Arial", font.Text);
        Assert.True(save.IsEnabled);
        font.Text = "Menlo";
        Dispatcher.UIThread.RunJobs();
        Assert.False(save.IsEnabled);
    }

    [AvaloniaFact]
    public void ThemeIsOneRadioGroupStagedUntilSave()
    {
        ThemePreference? saved = null;
        var dialog = new SettingsDialog(
            Languages.System,
            "Inter",
            ThemePreference.Light,
            isHiddenAndSystem: false,
            showWindowsHideMode: false,
            DisplayZone.System,
            (_, _, _, theme, _) =>
            {
                saved = theme;
                return Task.FromResult<Message?>(null);
            });
        var radios = dialog.GetLogicalDescendants().OfType<RadioButton>().ToList();
        var save = dialog.GetLogicalDescendants().OfType<Button>()
            .Single(button => Equals(button.Tag, "save"));

        // System names the computer's appearance now (app-chrome conventions, Theme).
        var appearance = Avalonia.Application.Current!.PlatformSettings!.GetColorValues().ThemeVariant;
        Assert.Equal(new[] { SettingsDialog.SystemThemeLabel(appearance), "Light", "Dark" }, radios.Select(radio => radio.Content as string));
        Assert.Single(radios.Select(radio => radio.Parent).Distinct());
        Assert.Equal(English.Of("settings.themeLight"), radios.Single(radio => radio.IsChecked == true).Content);
        Assert.False(save.IsEnabled);

        radios[2].IsChecked = true;
        Assert.True(save.IsEnabled);
        Assert.Null(saved);

        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(dialog.Accepted);
        Assert.Equal(ThemePreference.Dark, saved);
    }

    private static (MainWindowViewModel Vm, FakeSettingsStore Settings, Window Owner, SettingsDialog Dialog) OpenDirtySettings()
    {
        var settings = new FakeSettingsStore();
        var vm = new MainWindowViewModel(new BoundedVisibility(new FakeVisibilityService()),
            new FakeJsonStore<System.Collections.Generic.List<PathEntry>>(), settings, settings.Load().Value,
            new FakeJsonStore<AppState>(), new AppState(), (new FakeJsonStore<System.Collections.Generic.List<PathEntry>>()).Load().Value);
        var owner = new Window();
        owner.Show();
        var dialog = new SettingsDialog(vm.Language, vm.UiFontFamily, vm.Theme, vm.IsHiddenAndSystem,
            showWindowsHideMode: false, vm.DisplayTimeZone, vm.TryApplySettingsAsync);
        _ = dialog.ShowDialog(owner);
        dialog.GetLogicalDescendants().OfType<TextBox>().Single().Text = "Menlo";
        Dispatcher.UIThread.RunJobs();
        return (vm, settings, owner, dialog);
    }

    [AvaloniaFact]
    public async Task TheTimeZoneListStartsWithSystemNamingItsZoneAndAChoiceIsSaved()
    {
        string? saved = null;
        var dialog = new SettingsDialog(Languages.System, "Inter", ThemePreference.System, false, false,
            DisplayZone.System,
            (_, _, _, _, timeZone) =>
            {
                saved = timeZone;
                return Task.FromResult<Message?>(null);
            });
        var zones = dialog.GetLogicalDescendants().OfType<ComboBox>().Last();
        var options = zones.Items.OfType<SettingsDialog.ZoneOption>().ToList();
        var save = dialog.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Tag, "save"));

        Assert.Equal(DisplayZone.System, options[0].Value);
        Assert.Equal(Localizer.T("settings.timeZoneSystem", ("zone", DisplayZone.SystemZoneName())), options[0].Name);
        Assert.Equal(DisplayZone.Zones, options.Skip(1).Select(option => option.Value));
        Assert.Same(options[0], zones.SelectedItem);
        Assert.False(save.IsEnabled);

        zones.SelectedItem = options.Single(option => option.Value == "Asia/Tokyo");
        Assert.True(save.IsEnabled);
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Asia/Tokyo", saved);
    }

    [AvaloniaFact]
    public void CloseWindowShortcutOnADirtyDraftAsksLikeTheCloseButton()
    {
        using var speaking = PathHide.Tests.I18n.TestLanguage.Speaking("en");
        var (_, settings, owner, dialog) = OpenDirtySettings();
        try
        {
            CloseWindowShortcutTests.PressCloseWindow(dialog);

            Assert.Equal(English.Of("dialog.discardTitle"), dialog.OwnedWindows.OfType<ConfirmDialog>().Single().Title);
            Assert.True(dialog.IsVisible);
            Assert.Equal(0, settings.SaveCount);
        }
        finally
        {
            owner.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task KeepEditingRetainsTheDraftAndDiscardClosesWithoutSaving()
    {
        using var speaking = PathHide.Tests.I18n.TestLanguage.Speaking("en");
        var (_, settings, owner, dialog) = OpenDirtySettings();
        try
        {
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
            var question = dialog.OwnedWindows.OfType<ConfirmDialog>().Single();
            Assert.Equal(English.Of("dialog.discardTitle"), question.Title);
            var keep = question.GetLogicalDescendants().OfType<Button>()
                .Single(button => Equals(button.Tag, "cancel"));
            Assert.Equal(English.Of("dialog.keepEditing"), keep.Content);
            Assert.True(keep.IsDefault);
            Assert.True(keep.IsFocused);
            keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.IsVisible);
            Assert.Equal("Menlo", dialog.GetLogicalDescendants().OfType<TextBox>().Single().Text);
            Assert.Equal(0, settings.SaveCount);

            dialog.Close();
            Dispatcher.UIThread.RunJobs();
            question = dialog.OwnedWindows.OfType<ConfirmDialog>().Single();
            question.GetLogicalDescendants().OfType<Button>()
                .Single(button => Equals(button.Tag, "confirm"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            Assert.False(dialog.IsVisible);
            Assert.False(dialog.Accepted);
            Assert.Equal(0, settings.SaveCount);
        }
        finally
        {
            owner.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task QuittingWithADirtyDraftDiscardsItWithoutWritingConfig()
    {
        // The draft lives only in the dialog: the main window closing at quit closes it without asking,
        // and nothing is saved that Save did not save.
        var (vm, settings, owner, dialog) = OpenDirtySettings();

        owner.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(await vm.QuitAsync());

        Assert.False(dialog.IsVisible);
        Assert.False(dialog.Accepted);
        Assert.Equal(0, settings.SaveCount);
    }

    [AvaloniaFact]
    public async Task QuittingAfterSaveKeepsWhatSaveWrote()
    {
        var (vm, settings, owner, dialog) = OpenDirtySettings();
        dialog.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Tag, "save"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        owner.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(await vm.QuitAsync());

        Assert.Equal(1, settings.SaveCount);
        Assert.Equal("Menlo", settings.Value.UiFontFamily);
    }
}
