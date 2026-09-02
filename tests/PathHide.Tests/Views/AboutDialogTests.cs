using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class AboutDialogTests
{
    [AvaloniaFact]
    public void External_launch_failure_is_retained_locally_without_exposing_diagnostics()
    {
        const string hostile = "EACCES IPC /private/tmp/PATHHIDE-ABOUT-SENTINEL";
        var dialog = new AboutDialog(_ => false);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        var before = dialog.Bounds.Height;
        var externalButton = dialog.GetVisualDescendants()
            .OfType<Button>()
            .First(button => button.Classes.Contains("utility"));

        externalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var result = dialog.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => AutomationProperties.GetLiveSetting(border) == AutomationLiveSetting.Assertive);
        var message = result.GetVisualDescendants().OfType<TextBlock>().Single().Text ?? string.Empty;
        var dismiss = result.GetVisualDescendants().OfType<Button>().Single();
        var close = dialog.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, "Close"));

        Assert.True(result.IsVisible);
        Assert.DoesNotContain(hostile, message, System.StringComparison.Ordinal);
        Assert.True(dialog.Bounds.Height > before);
        Assert.True(close.IsVisible);
        var closeBottom = close.TranslatePoint(new Point(close.Bounds.Width, close.Bounds.Height), dialog);
        Assert.NotNull(closeBottom);
        Assert.True(closeBottom.Value.Y <= dialog.ClientSize.Height);

        dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(result.IsVisible);
        dialog.Close();
    }
}
