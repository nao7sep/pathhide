using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using PathHide.I18n;
using PathHide.ViewModels;

namespace PathHide.Views;

/// <summary>
/// The question a quit the user started asks when a save of theirs did not land: Cancel, Retry (the
/// default) or Quit anyway. Cancel, Escape and closing the dialog keep the app open
/// (unsaved-edits-conventions, Quitting; modal-dialog-conventions, a labelled dismiss).
/// </summary>
public sealed class UnsavedQuitDialog : DialogBase
{
    internal UnsavedQuitDialog(IReadOnlyList<Message> lines)
    {
        Width = 440;
        // Rendered once, as it is built: it is modal, so the language cannot change while it is up.
        Title = Localizer.Of(Message.Of("quit.unsavedTitle"));

        var body = new StackPanel { Spacing = 10 };
        body.Children.AddRange(lines.Select(line => new TextBlock
        {
            Text = Localizer.Of(line),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        }));
        SetContent(body);

        var buttons = SetButtons(
        [
            new DialogButton("common.cancel", "cancel"),
            new DialogButton("quit.retry", "retry", DialogButtonKind.Primary) { IsDefault = true },
            new DialogButton("quit.quitAnyway", "quitAnyway", DialogButtonKind.Danger),
        ]);

        SetInitialFocus(buttons["retry"]);
    }

    internal UnsavedQuitChoice Choice => ResultTag switch
    {
        "retry" => UnsavedQuitChoice.Retry,
        "quitAnyway" => UnsavedQuitChoice.QuitAnyway,
        _ => UnsavedQuitChoice.KeepOpen,
    };

    public static async Task<UnsavedQuitChoice> AskAsync(Window owner, IReadOnlyList<Message> lines)
    {
        var dialog = new UnsavedQuitDialog(lines);
        await dialog.ShowBoundedAsync(owner);
        return dialog.Choice;
    }
}
