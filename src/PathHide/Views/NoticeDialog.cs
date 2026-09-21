using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using PathHide.I18n;

namespace PathHide.Views;

/// <summary>
/// A single-button informational dialog: a wrapped message with a Close button.
/// Used for notices the user must see once (a quarantined store) rather than
/// choices — the shared ConfirmDialog handles those.
/// </summary>
public sealed class NoticeDialog : DialogBase
{
    private NoticeDialog(Message title, Message message)
    {
        Width = 440;
        // Rendered once, as it is built: a notice is modal, or it is the application's only window,
        // and in neither case can the language change while it is up.
        Title = Localizer.Of(title);

        SetContent(new TextBlock
        {
            Text = Localizer.Of(message),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });

        var buttons = SetButtons([new DialogButton("common.close", "close", DialogButtonKind.Primary) { IsDefault = true }]);
        SetInitialFocus(buttons["close"]);
    }

    public static Task ShowAsync(Window owner, Message title, Message message) =>
        new NoticeDialog(title, message).ShowBoundedAsync(owner);

    /// <summary>
    /// A startup failure notice used as the main window. Closing it ends the app.
    /// </summary>
    public static Window CreateStartupFailure(Message title, Message message)
    {
        var dialog = new NoticeDialog(title, message);
        dialog.BoundHeightToScreen();
        ShowAsOnlyWindow(dialog);
        return dialog;
    }
}
