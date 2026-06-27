using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using PathHide.Models;

namespace PathHide.Views;

public sealed class SettingsDialog : DialogBase
{
    private readonly TextBox _uiFontBox;
    private readonly CheckBox _hiddenAndSystemCheckBox;
    private readonly string _originalUiFont;
    private readonly bool _originalIsHiddenAndSystem;
    private readonly Button _saveButton;

    public bool Accepted => ResultTag == "save";
    public bool IsHiddenAndSystem => _hiddenAndSystemCheckBox.IsChecked == true;
    public string UiFontFamily => (_uiFontBox.Text ?? string.Empty).Trim();

    public SettingsDialog(string uiFontFamily, bool isHiddenAndSystem, bool showWindowsHideMode)
    {
        _originalUiFont = (uiFontFamily ?? string.Empty).Trim();
        _originalIsHiddenAndSystem = isHiddenAndSystem;
        Width = 500;
        Title = "Settings";

        _uiFontBox = new TextBox { Text = uiFontFamily, PlaceholderText = AppSettings.DefaultUiFontFamily };
        _hiddenAndSystemCheckBox = new CheckBox
        {
            Content = "Also set System attribute when hiding (Windows)",
            IsChecked = isHiddenAndSystem,
        };

        var fontHint = new TextBlock
        {
            Text = "Comma-separated; the first installed family is used. Blank uses Inter.",
            FontSize = 12,
        };
        fontHint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush");

        // UI font (cross-platform appearance) leads; the Windows-only hide mode follows and shows only
        // where it applies, so the dialog is never cluttered with a setting that does nothing here.
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "UI font", FontWeight = FontWeight.SemiBold, FontSize = 14 },
                _uiFontBox,
                fontHint,
            },
        };

        if (showWindowsHideMode)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Windows Hide Mode",
                FontWeight = FontWeight.SemiBold,
                FontSize = 14,
            });
            panel.Children.Add(_hiddenAndSystemCheckBox);
        }

        SetContent(panel);
        var buttons = SetButtons(
        [
            new DialogButton("Cancel", "cancel"),
            new DialogButton("Save", "save", DialogButtonKind.Primary) { IsDefault = true },
        ]);
        _saveButton = buttons["save"];

        SetInitialFocus(_uiFontBox);

        // Wire change handlers only after _saveButton exists, so a change raised during setup can never
        // run UpdateSaveState against a null button. Then seed the state.
        _uiFontBox.TextChanged += (_, _) => UpdateSaveState();
        _hiddenAndSystemCheckBox.IsCheckedChanged += (_, _) => UpdateSaveState();
        UpdateSaveState();
    }

    // Save commits a draft, so the shell's dirty guard prompts on dismiss and Save stays disabled until
    // the draft actually differs from the persisted values (the conventions' dirty gate for explicit
    // commit buttons). Both fields are always valid, so dirtiness alone gates the commit.
    protected override bool HasUnsavedChanges =>
        UiFontFamily != _originalUiFont || IsHiddenAndSystem != _originalIsHiddenAndSystem;

    private void UpdateSaveState() => _saveButton.IsEnabled = HasUnsavedChanges;
}
