using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace PathHide.Views;

/// <summary>
/// A single-button informational dialog: a wrapped message with a Close button.
/// Used for notices the user must see once (a quarantined store) rather than
/// choices — the shared ConfirmDialog handles those.
/// </summary>
public sealed class NoticeDialog : DialogBase
{
    private NoticeDialog(string title, string message)
    {
        Width = 440;
        Title = title;

        SetContent(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });

        var buttons = SetButtons([new DialogButton("Close", "close", DialogButtonKind.Primary) { IsDefault = true }]);
        SetInitialFocus(buttons["close"]);
    }

    public static Task ShowAsync(Window owner, string title, string message) =>
        new NoticeDialog(title, message).ShowDialog(owner);
}
