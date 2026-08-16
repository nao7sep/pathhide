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

    /// <summary>
    /// The same notice, built to stand alone as the application's main window. Used when a
    /// store cannot be loaded AND cannot be set aside: the app must not reset over the
    /// preserved bytes, so it halts — and a halt has to reach the user, which before a main
    /// window exists means becoming one (storage-path conventions: both branches report).
    /// Closing it ends the app, since the lifetime shuts down with its last window.
    /// </summary>
    public static Window CreateStartupFailure(string title, string message) =>
        new NoticeDialog(title, message);
}
