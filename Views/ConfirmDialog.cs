using Avalonia.Controls;
using Avalonia.Media;

namespace PathHide.Views;

public sealed class ConfirmDialog : DialogBase
{
    public ConfirmDialog(string title, string message)
    {
        Width = 400;
        Title = title;

        SetContent(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });

        var buttons = SetButtons([
            ("Yes", "yes", true),
            ("No", "no", false),
        ]);
        SetInitialFocus(buttons["no"]);
    }

    public bool Confirmed => ResultTag == "yes";
}
