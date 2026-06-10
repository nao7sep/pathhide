using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

namespace PathHide.Views;

/// <summary>
/// The app's shared confirmation dialog. Shows a message with a specific, danger-styled
/// action button (for example <c>Remove</c> or <c>Discard</c>) beside a neutral Cancel.
/// Cancel is focused and Enter-activated, so a stray keypress or click never confirms a
/// destructive action.
/// </summary>
public sealed class ConfirmDialog : DialogBase
{
    private ConfirmDialog(string title, string message, string confirmLabel)
    {
        Width = 400;
        Title = title;

        SetContent(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });

        var buttons = SetButtons(
        [
            new DialogButton("Cancel", "cancel") { IsDefault = true },
            new DialogButton(confirmLabel, "confirm", DialogButtonKind.Danger),
        ]);

        SetInitialFocus(buttons["cancel"]);
    }

    private bool Confirmed => ResultTag == "confirm";

    /// <summary>
    /// Shows a modal destructive confirmation owned by <paramref name="owner"/>. Returns true
    /// only if the user chooses the destructive action; Cancel, Escape, and window close all
    /// resolve to false, so the promise always settles on the safe path.
    /// </summary>
    public static async Task<bool> ConfirmDestructiveAsync(Window owner, string title, string message, string confirmLabel)
    {
        var dialog = new ConfirmDialog(title, message, confirmLabel);
        await dialog.ShowDialog(owner);
        return dialog.Confirmed;
    }
}
