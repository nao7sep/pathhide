using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using PathHide.Models;
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
            (_, _, _, _) => FailurePresentation.SettingsSave(new System.IO.IOException("Access to /private/test/config.tmp is denied.")));
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
            (_, _, _, theme) =>
            {
                saved = theme;
                return null;
            });
        var radios = dialog.GetLogicalDescendants().OfType<RadioButton>().ToList();
        var save = dialog.GetLogicalDescendants().OfType<Button>()
            .Single(button => Equals(button.Tag, "save"));

        Assert.Equal(new[] { "System", "Light", "Dark" }, radios.Select(radio => radio.Content as string));
        Assert.Single(radios.Select(radio => radio.Parent).Distinct());
        Assert.Equal("Light", radios.Single(radio => radio.IsChecked == true).Content);
        Assert.False(save.IsEnabled);

        radios[2].IsChecked = true;
        Assert.True(save.IsEnabled);
        Assert.Null(saved);

        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(dialog.Accepted);
        Assert.Equal(ThemePreference.Dark, saved);
    }
}
