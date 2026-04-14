using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PathHide.Views;

public sealed class SettingsDialog : DialogBase
{
    private readonly CheckBox _hiddenAndSystemCheckBox;

    public bool Accepted => ResultTag == "save";
    public bool IsHiddenAndSystem => _hiddenAndSystemCheckBox.IsChecked == true;

    public SettingsDialog(bool isHiddenAndSystem)
    {
        Width = 500;
        Title = "Settings";

        _hiddenAndSystemCheckBox = new CheckBox
        {
            Content = "Also set System attribute when hiding (Windows)",
            IsChecked = isHiddenAndSystem,
        };

        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Windows Hide Mode",
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 14,
                },
                _hiddenAndSystemCheckBox,
            },
        };

        SetContent(panel);
        SetButtons([
            ("Save", "save", true),
            ("Cancel", "cancel", false),
        ]);
        SetInitialFocus(_hiddenAndSystemCheckBox);
    }
}
