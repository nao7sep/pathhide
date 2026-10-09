using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using PathHide.I18n;
using PathHide.ViewModels;
using PathHide.Views;
using Xunit;

namespace PathHide.Tests.Views;

public sealed class UnsavedQuitDialogTests
{
    private static UnsavedQuitDialog Dialog() =>
        new([Message.Of("failure.pathListSave"), Message.Of("quit.unsavedMessage")]);

    [AvaloniaFact]
    public void It_offers_a_labelled_Cancel_before_Retry_and_Quit_anyway()
    {
        var dialog = Dialog();

        var tags = dialog.GetLogicalDescendants().OfType<Button>()
            .Select(button => button.Tag as string).Where(tag => tag is not null);

        Assert.Equal(["cancel", "retry", "quitAnyway"], tags);
    }

    [AvaloniaFact]
    public void Cancel_keeps_the_app_open()
    {
        var dialog = Dialog();
        var owner = new Window();
        owner.Show();
        _ = dialog.ShowDialog(owner);

        dialog.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Tag, "cancel"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(UnsavedQuitChoice.KeepOpen, dialog.Choice);
        owner.Close();
    }
}
