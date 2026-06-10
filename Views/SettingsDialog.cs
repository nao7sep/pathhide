using Avalonia.Controls;
using Avalonia.Media;

namespace PathHide.Views;

public sealed class SettingsDialog : DialogBase
{
    private readonly CheckBox _hiddenAndSystemCheckBox;
    private readonly bool _originalIsHiddenAndSystem;
    private readonly Button _saveButton;

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
        var buttons = SetButtons(
        [
            new DialogButton("Cancel", "cancel"),
            new DialogButton("Save", "save", DialogButtonKind.Primary) { IsDefault = true },
        ]);
        _saveButton = buttons["save"];

        SetInitialFocus(_hiddenAndSystemCheckBox);

        // Wire the change handler only after _saveButton exists, so an IsCheckedChanged raised
        // during setup can never run UpdateSaveState against a null button. Then seed the state.
        _hiddenAndSystemCheckBox.IsCheckedChanged += (_, _) => UpdateSaveState();
        UpdateSaveState();
    }

    // Save commits a draft, so the shell's dirty guard prompts on dismiss and Save stays
    // disabled until the draft actually differs from the persisted value (the conventions'
    // dirty gate for explicit commit buttons). Validity is not a factor here — a checkbox
    // is always valid — so dirtiness alone gates the commit.
    protected override bool HasUnsavedChanges => IsHiddenAndSystem != _originalIsHiddenAndSystem;

    private void UpdateSaveState() => _saveButton.IsEnabled = HasUnsavedChanges;
}
