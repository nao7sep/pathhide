using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using PathHide.I18n;

namespace PathHide.Views;

/// <summary>
/// The app's shared confirmation dialog. Shows a message with a specific, danger-styled
/// action button (for example <c>Remove</c> or <c>Discard</c>) beside a neutral Cancel.
/// Cancel is focused and Enter-activated, so a stray keypress or click never confirms a
/// destructive action.
/// </summary>
public sealed class ConfirmDialog : DialogBase
{
    private ConfirmDialog(Message title, Message message, string confirmLabelKey)
    {
        Width = 400;
        // A dialog's own words are rendered once, as it is built: it is modal, so the language cannot
        // change while it is up, and its title and message carry values the catalogue fills in.
        Title = Localizer.Of(title);

        SetContent(new TextBlock
        {
            Text = Localizer.Of(message),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });

        var buttons = SetButtons(
        [
            new DialogButton("common.cancel", "cancel") { IsDefault = true },
            new DialogButton(confirmLabelKey, "confirm", DialogButtonKind.Danger),
        ]);

        SetInitialFocus(buttons["cancel"]);
    }

    private bool Confirmed => ResultTag == "confirm";

    /// <summary>
    /// Shows a modal destructive confirmation owned by <paramref name="owner"/>. Returns true
    /// only if the user chooses the destructive action; Cancel, Escape, and window close all
    /// resolve to false, so the promise always settles on the safe path.
    /// </summary>
    public static async Task<bool> ConfirmDestructiveAsync(Window owner, Message title, Message message, string confirmLabelKey)
    {
        var dialog = new ConfirmDialog(title, message, confirmLabelKey);
        await dialog.ShowBoundedAsync(owner);
        return dialog.Confirmed;
    }
}
