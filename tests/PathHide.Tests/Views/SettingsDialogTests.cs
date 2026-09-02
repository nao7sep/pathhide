using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class SettingsDialogTests
{
    [AvaloniaFact]
    public void FailedSaveKeepsDraftAndDiagnosticPathOutOfInlineMessage()
    {
        var dialog = new SettingsDialog(
            "Inter",
            isHiddenAndSystem: false,
            showWindowsHideMode: false,
            (_, _) => "Access to /private/test/config.tmp is denied.");
        var font = dialog.GetLogicalDescendants().OfType<TextBox>().Single();
        font.Text = "Menlo";
        var save = dialog.GetLogicalDescendants().OfType<Button>()
            .Single(button => Equals(button.Tag, "save"));

        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(dialog.Accepted);
        Assert.Equal("Menlo", font.Text);
        var error = dialog.GetLogicalDescendants().OfType<TextBlock>().Single(block =>
            block.IsVisible && block.Text?.Contains("could not be saved") == true);
        Assert.Contains("try again", error.Text);
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
}
