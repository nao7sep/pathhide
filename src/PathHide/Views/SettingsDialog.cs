using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Automation;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using PathHide.Controls;
using PathHide.I18n;
using PathHide.Models;

namespace PathHide.Views;

public sealed class SettingsDialog : DialogBase
{
    private static readonly (ThemePreference Value, string LabelKey)[] ThemeChoices =
    [
        (ThemePreference.System, "settings.themeSystem"),
        (ThemePreference.Light, "settings.themeLight"),
        (ThemePreference.Dark, "settings.themeDark"),
    ];

    private readonly ComboBox _languageBox;
    private readonly ImeTextBox _uiFontBox;
    private readonly CheckBox _hiddenAndSystemCheckBox;
    private readonly IReadOnlyList<RadioButton> _themeButtons;
    private readonly string _originalLanguage;
    private readonly string _originalUiFont;
    private readonly bool _originalIsHiddenAndSystem;
    private readonly ThemePreference _originalTheme;
    private readonly Button _saveButton;
    private readonly TextBlock _saveError;
    private readonly Func<string, string, bool, ThemePreference, Message?> _trySave;

    public bool Accepted => ResultTag == "save";
    public string SelectedLanguage => ((LanguageOption)_languageBox.SelectedItem!).Value;
    public bool IsHiddenAndSystem => _hiddenAndSystemCheckBox.IsChecked == true;
    public string UiFontFamily => UiFontFamilyValue.Normalize(_uiFontBox.Text);
    public ThemePreference SelectedTheme =>
        _themeButtons.Zip(ThemeChoices).First(pair => pair.First.IsChecked == true).Second.Value;

    public SettingsDialog(
        string language,
        string uiFontFamily,
        ThemePreference theme,
        bool isHiddenAndSystem,
        bool showWindowsHideMode,
        Func<string, string, bool, ThemePreference, Message?> trySave)
    {
        _trySave = trySave;
        _originalLanguage = Languages.NormalizePreference(language);
        _originalUiFont = UiFontFamilyValue.Normalize(uiFontFamily);
        _originalIsHiddenAndSystem = isHiddenAndSystem;
        _originalTheme = theme;
        Width = 500;
        Localized.SetTitle(this, "settings.title");

        // System first, then each language by its own name, so a reader finds theirs whatever
        // language is showing. Applied on Save like every other field here.
        var languages = LanguageOption.All();
        _languageBox = new ComboBox
        {
            ItemsSource = languages,
            SelectedItem = LanguageOption.For(_originalLanguage, languages),
            DisplayMemberBinding = new Avalonia.Data.Binding(nameof(LanguageOption.Name)),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Localized.SetAutomationName(_languageBox, "settings.language");

        // The platform radio group: one tab stop, arrow keys move and select (composite-control
        // conventions). App-wide, applied on Save like every other field here. The buttons group by
        // their shared parent rather than a GroupName, which Avalonia tracks across every dialog not
        // yet in a window, so one Settings dialog's radios could uncheck another's.
        _themeButtons = ThemeChoices
            .Select(choice =>
            {
                var button = new RadioButton { IsChecked = choice.Value == theme };
                Localized.SetContent(button, choice.LabelKey);
                return button;
            })
            .ToList();
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        foreach (var button in _themeButtons)
            themeRow.Children.Add(button);
        Localized.SetAutomationName(themeRow, "settings.theme");
        var themeHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Localized.SetText(themeHint, "settings.themeHint");
        themeHint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush");

        _uiFontBox = new ImeTextBox { Text = uiFontFamily, PlaceholderText = AppSettings.DefaultUiFontFamily };
        // A sentence, so it wraps inside the dialog's fixed width rather than running past it.
        var hiddenAndSystemLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Localized.SetText(hiddenAndSystemLabel, "settings.hiddenAndSystem");
        _hiddenAndSystemCheckBox = new CheckBox { IsChecked = isHiddenAndSystem, Content = hiddenAndSystemLabel };

        var fontHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Localized.SetText(fontHint, "settings.uiFontHint");
        fontHint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush");

        // The checkbox label only covers the setting's ON behaviour. Hiding with it OFF
        // CLEARS the System attribute, and showing clears it unconditionally — so a path that
        // carried System for its own reasons loses it, and PathHide keeps no record to put it
        // back. Advertising the operation as reversible without saying this was the gap.
        var hideModeHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Localized.SetText(hideModeHint, "settings.hideModeHint");
        hideModeHint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush");

        // Language and appearance (theme, then UI font) lead; the Windows-only hide mode follows and
        // shows only where it applies, so the dialog is never cluttered with a setting that does
        // nothing here.
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                SectionHeader("settings.language"),
                _languageBox,
                SectionHeader("settings.theme"),
                themeRow,
                themeHint,
                SectionHeader("settings.uiFont"),
                _uiFontBox,
                fontHint,
            },
        };

        if (showWindowsHideMode)
        {
            panel.Children.Add(SectionHeader("settings.windowsHideMode"));
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
            new DialogButton("common.cancel", "cancel"),
            new DialogButton("common.save", "save", DialogButtonKind.Primary) { IsDefault = true },
        ]);
        _saveButton = buttons["save"];

        SetInitialFocus(_themeButtons.First(button => button.IsChecked == true));

        // Wire change handlers only after _saveButton exists, so a change raised during setup can never
        // run UpdateSaveState against a null button. Then seed the state.
        _languageBox.SelectionChanged += (_, _) => UpdateSaveState();
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
        SelectedLanguage != _originalLanguage
        || UiFontFamily != _originalUiFont
        || IsHiddenAndSystem != _originalIsHiddenAndSystem
        || SelectedTheme != _originalTheme;

    protected override bool TryCommit(string tag)
    {
        if (tag != "save")
            return true;

        _saveError.IsVisible = false;
        _saveError.Text = string.Empty;
        var failure = _trySave(SelectedLanguage, UiFontFamily, IsHiddenAndSystem, SelectedTheme);
        if (failure is null)
            return true;

        _saveError.Text = Localizer.Of(failure);
        _saveError.IsVisible = true;
        return false;
    }

    private void UpdateSaveState() => _saveButton.IsEnabled = HasUnsavedChanges;

    private static TextBlock SectionHeader(string key)
    {
        var header = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 14 };
        Localized.SetText(header, key);
        return header;
    }
}
