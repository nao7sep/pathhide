using Avalonia.Controls;
using Avalonia.Media;

namespace PathHide.Views;

public sealed class ConfirmDialog : DialogBase
{
    public ConfirmDialog(string title, string message)
    {
        Title = title;

        SetContent(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
        });

        SetButtons([
            ("Yes", "yes", true),
            ("No", "no", false),
        ]);
    }

    public bool Confirmed => ResultTag == "yes";
}
