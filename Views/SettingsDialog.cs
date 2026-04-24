using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PathHide.Views;

public sealed class SettingsDialog : DialogBase
{
    private readonly CheckBox _hiddenAndSystemCheckBox;
    private readonly bool _originalIsHiddenAndSystem;
    private bool _skipDirtyCheck;

    public bool Accepted => ResultTag == "save";
    public bool IsHiddenAndSystem => _hiddenAndSystemCheckBox.IsChecked == true;

    public SettingsDialog(bool isHiddenAndSystem)
    {
        _originalIsHiddenAndSystem = isHiddenAndSystem;
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
            ("Cancel", "cancel", false),
            ("Save", "save", true),
        ]);
        SetInitialFocus(_hiddenAndSystemCheckBox);

        Closing += OnClosing;
    }

    private bool IsDirty => _hiddenAndSystemCheckBox.IsChecked != _originalIsHiddenAndSystem;

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_skipDirtyCheck || ResultTag == "save" || !IsDirty)
            return;

        // Cancel the synchronous close and run the async confirmation instead.
        e.Cancel = true;
        _ = ConfirmDiscardAsync();
    }

    private async Task ConfirmDiscardAsync()
    {
        var dialog = new ConfirmDialog("Discard Changes", "You have unsaved changes. Discard them and close?");
        await dialog.ShowDialog(this);

        if (dialog.Confirmed)
        {
            _skipDirtyCheck = true;
            Close();
        }
    }
}
