using Avalonia.Controls;
using Avalonia.Media;

namespace PathHide.Views;

/// <summary>
/// Keyboard-shortcuts help. Opened from the menu button or via Ctrl+/ (the
/// convention's shortcuts-help accelerator). Settings is Windows-only; the rest are
/// cross-platform.
/// </summary>
public sealed class ShortcutsDialog : DialogBase
{
    private static readonly (string Key, string Description)[] Shortcuts =
    [
        ("↑ / ↓", "Move the selection up or down the list"),
        ("← / →", "Move focus between the action buttons"),
        ("Delete", "Remove the selected entries from the list"),
        ("Ctrl + ,", "Open Settings (Windows only)"),
        ("Drag and drop", "Add files or folders by dropping them on the window"),
    ];

    public ShortcutsDialog()
    {
        Width = 440;
        Title = "Keyboard Shortcuts";

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowSpacing = 10,
            ColumnSpacing = 18,
        };

        for (var row = 0; row < Shortcuts.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var (key, description) = Shortcuts[row];

            var keyBlock = new TextBlock { Text = key, FontWeight = FontWeight.SemiBold };
            Grid.SetRow(keyBlock, row);
            Grid.SetColumn(keyBlock, 0);

            var descBlock = new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
            };
            Grid.SetRow(descBlock, row);
            Grid.SetColumn(descBlock, 1);

            grid.Children.Add(keyBlock);
            grid.Children.Add(descBlock);
        }

        SetContent(grid);
        var buttons = SetButtons(
        [
            new DialogButton("Close", "close", DialogButtonKind.Primary) { IsDefault = true },
        ]);
        SetInitialFocus(buttons["close"]);
    }
}
