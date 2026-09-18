using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using PathHide.Controls;
using PathHide.Models;

namespace PathHide.Views;

public sealed class SettingsDialog : DialogBase
{
    private static readonly (ThemePreference Value, string Label)[] ThemeChoices =
    [
        (ThemePreference.System, "System"),
        (ThemePreference.Light, "Light"),
        (ThemePreference.Dark, "Dark"),
    ];

    private readonly ImeTextBox _uiFontBox;
    private readonly CheckBox _hiddenAndSystemCheckBox;
    private readonly IReadOnlyList<RadioButton> _themeButtons;
    private readonly string _originalUiFont;
    private readonly bool _originalIsHiddenAndSystem;
    private readonly ThemePreference _originalTheme;
    private readonly Button _saveButton;
    private readonly TextBlock _saveError;
    private readonly Func<string, bool, ThemePreference, string?> _trySave;

    public bool Accepted => ResultTag == "save";
    public bool IsHiddenAndSystem => _hiddenAndSystemCheckBox.IsChecked == true;
    public string UiFontFamily => UiFontFamilyValue.Normalize(_uiFontBox.Text);
    public ThemePreference SelectedTheme =>
        _themeButtons.Zip(ThemeChoices).First(pair => pair.First.IsChecked == true).Second.Value;

    public SettingsDialog(
        string uiFontFamily,
        ThemePreference theme,
        bool isHiddenAndSystem,
        bool showWindowsHideMode,
        Func<string, bool, ThemePreference, string?> trySave)
    {
        _trySave = trySave;
        _originalUiFont = UiFontFamilyValue.Normalize(uiFontFamily);
        _originalIsHiddenAndSystem = isHiddenAndSystem;
        _originalTheme = theme;
        Width = 500;
        Title = "Settings";

        // The platform radio group: one tab stop, arrow keys move and select (composite-control
        // conventions). App-wide, applied on Save like every other field here. The buttons group by
        // their shared parent rather than a GroupName, which Avalonia tracks across every dialog not
        // yet in a window, so one Settings dialog's radios could uncheck another's.
        _themeButtons = ThemeChoices
            .Select(choice => new RadioButton
            {
                Content = choice.Label,
                IsChecked = choice.Value == theme,
            })
            .ToList();
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        foreach (var button in _themeButtons)
            themeRow.Children.Add(button);
        AutomationProperties.SetName(themeRow, "Theme");
        var themeHint = new TextBlock { Text = "System follows the OS appearance.", FontSize = 12 };
        themeHint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush");

        _uiFontBox = new ImeTextBox { Text = uiFontFamily, PlaceholderText = AppSettings.DefaultUiFontFamily };
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

        // The checkbox label only covers the setting's ON behaviour. Hiding with it OFF
        // CLEARS the System attribute, and showing clears it unconditionally — so a path that
        // carried System for its own reasons loses it, and PathHide keeps no record to put it
        // back. Advertising the operation as reversible without saying this was the gap.
        var hideModeHint = new TextBlock
        {
            Text = "PathHide clears the System attribute when showing, and when hiding with this "
                 + "off. A file that already had it will not get it back.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        hideModeHint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush");

        // Appearance (theme, then UI font) leads; the Windows-only hide mode follows and shows only
        // where it applies, so the dialog is never cluttered with a setting that does nothing here.
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Theme", FontWeight = FontWeight.SemiBold, FontSize = 14 },
                themeRow,
                themeHint,
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
            panel.Children.Add(hideModeHint);
        }

        _saveError = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };
        _saveError[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("DangerTextBrush");
        AutomationProperties.SetLiveSetting(_saveError, AutomationLiveSetting.Assertive);
        panel.Children.Add(_saveError);

        SetContent(panel);
        var buttons = SetButtons(
        [
            new DialogButton("Cancel", "cancel"),
            new DialogButton("Save", "save", DialogButtonKind.Primary) { IsDefault = true },
        ]);
        _saveButton = buttons["save"];

        SetInitialFocus(_themeButtons.First(button => button.IsChecked == true));

        // Wire change handlers only after _saveButton exists, so a change raised during setup can never
        // run UpdateSaveState against a null button. Then seed the state.
        _uiFontBox.TextChanged += (_, _) => UpdateSaveState();
        _hiddenAndSystemCheckBox.IsCheckedChanged += (_, _) => UpdateSaveState();
        foreach (var button in _themeButtons)
            button.IsCheckedChanged += (_, _) => UpdateSaveState();
        UpdateSaveState();
    }

    // Save commits a draft, so the shell's dirty guard prompts on dismiss and Save stays disabled until
    // the draft actually differs from the persisted values (the conventions' dirty gate for explicit
    // commit buttons). Every field is always valid, so dirtiness alone gates the commit.
    protected override bool HasUnsavedChanges =>
        UiFontFamily != _originalUiFont
        || IsHiddenAndSystem != _originalIsHiddenAndSystem
        || SelectedTheme != _originalTheme;

    protected override bool TryCommit(string tag)
    {
        if (tag != "save")
            return true;

        _saveError.IsVisible = false;
        _saveError.Text = string.Empty;
        var failure = _trySave(UiFontFamily, IsHiddenAndSystem, SelectedTheme);
        if (failure is null)
            return true;

        _saveError.Text = "Settings could not be saved. Your changes are still here; try again.";
        _saveError.IsVisible = true;
        return false;
    }

    private void UpdateSaveState() => _saveButton.IsEnabled = HasUnsavedChanges;
}
