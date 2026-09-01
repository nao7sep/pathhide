using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
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
    }
}
