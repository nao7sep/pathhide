using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
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
    private readonly ComboBox _timeZoneBox;
    private readonly ImeTextBox _uiFontBox;
    private readonly TextBlock _uiFontMissing;
    private readonly CheckBox _hiddenAndSystemCheckBox;
    private readonly IReadOnlyList<RadioButton> _themeButtons;
    private string _originalLanguage;
    private string _originalUiFont;
    private bool _originalIsHiddenAndSystem;
    private ThemePreference _originalTheme;
    private string _originalTimeZone;
    private readonly Button _saveButton;
    private readonly TextBlock _saveError;
    private readonly Func<string, string, bool, ThemePreference, string, Task<Message?>> _trySave;

    public bool Accepted => ResultTag == "save";
    public string SelectedLanguage => ((LanguageOption)_languageBox.SelectedItem!).Value;
    public bool IsHiddenAndSystem => _hiddenAndSystemCheckBox.IsChecked == true;
    public string UiFontFamily => UiFontFamilyValue.Normalize(_uiFontBox.Text);
    public ThemePreference SelectedTheme =>
        _themeButtons.Zip(ThemeChoices).First(pair => pair.First.IsChecked == true).Second.Value;
    public string SelectedTimeZone => ((ZoneOption)_timeZoneBox.SelectedItem!).Value;

    /// <summary>A line in the time-zone list: the value saved, and the name shown for it.</summary>
    public sealed record ZoneOption(string Value, string Name);

    public SettingsDialog(
        string language,
        string uiFontFamily,
        ThemePreference theme,
        bool isHiddenAndSystem,
        bool showWindowsHideMode,
        string timeZone,
        Func<string, string, bool, ThemePreference, string, Task<Message?>> trySave)
    {
        _trySave = trySave;
        _originalLanguage = Languages.NormalizePreference(language);
        _originalUiFont = UiFontFamilyValue.Normalize(uiFontFamily);
        _originalIsHiddenAndSystem = isHiddenAndSystem;
        _originalTheme = theme;
        _originalTimeZone = DisplayZone.NormalizePreference(timeZone);
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

        // System first, named with the zone it resolves to now, then the platform's IANA zones; chosen,
        // never typed (timestamp-conventions, Time zones). Applied on Save like every other field here.
        IReadOnlyList<ZoneOption> zones =
        [
            new(DisplayZone.System, Localizer.T("settings.timeZoneSystem", ("zone", DisplayZone.SystemZoneName()))),
            .. DisplayZone.Zones.Select(zone => new ZoneOption(zone, zone)),
        ];
        _timeZoneBox = new ComboBox
        {
            ItemsSource = zones,
            SelectedItem = zones.FirstOrDefault(zone => zone.Value == _originalTimeZone) ?? zones[0],
            DisplayMemberBinding = new Avalonia.Data.Binding(nameof(ZoneOption.Name)),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Localized.SetAutomationName(_timeZoneBox, "settings.timeZone");
        var timeZoneHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Localized.SetText(timeZoneHint, "settings.timeZoneHint");
        timeZoneHint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush");

        // Native radios retain their selection and focus visuals. Their parent supplies one Tab stop
        // and arrow selection, which Avalonia does not supply for a radio group. Changes apply on Save.
        // The buttons group by their shared parent rather than a GroupName, tracked across every dialog not
        // yet in a window, so one Settings dialog's radios could uncheck another's.
        _themeButtons = ThemeChoices
            .Select(choice =>
            {
                var button = new RadioButton { IsChecked = choice.Value == theme };
                if (choice.Value != ThemePreference.System)
                    Localized.SetContent(button, choice.LabelKey);
                return button;
            })
            .ToList();
        // System names the appearance it resolves to now, and follows the computer while the dialog is
        // open (app-chrome conventions, Theme).
        ShowSystemTheme();
        if (Avalonia.Application.Current?.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += OnColorValuesChanged;
            Closed += (_, _) => platform.ColorValuesChanged -= OnColorValuesChanged;
        }
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        foreach (var button in _themeButtons)
            themeRow.Children.Add(button);
        KeyboardNavigation.SetTabNavigation(themeRow, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetTabOnceActiveElement(themeRow, _themeButtons.First(button => button.IsChecked == true));
        themeRow.KeyDown += OnThemeKeyDown;
        Localized.SetAutomationName(themeRow, "settings.theme");
        var themeHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Localized.SetText(themeHint, "settings.themeHint");
        themeHint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush");

        _uiFontBox = new ImeTextBox { Text = uiFontFamily, PlaceholderText = AppSettings.DefaultUiFontName };
        // A sentence, so it wraps inside the dialog's fixed width rather than running past it.
        var hiddenAndSystemLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Localized.SetText(hiddenAndSystemLabel, "settings.hiddenAndSystem");
        _hiddenAndSystemCheckBox = new CheckBox { IsChecked = isHiddenAndSystem, Content = hiddenAndSystemLabel };

        var fontHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Localized.SetText(fontHint, "settings.uiFontHint");
        fontHint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush");

        // Saving is still allowed: the entry may name a font installed later, and the note says what
        // shows meanwhile instead of the fallback happening silently.
        _uiFontMissing = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Name = "UiFontMissing" };
        Localized.SetText(_uiFontMissing, "settings.uiFontMissing");
        _uiFontMissing[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("WarningTextBrush");
        UpdateUiFontMissing();

        // The checkbox label only covers the setting's ON behaviour. Hiding with it OFF
        // CLEARS the System attribute, and showing clears it unconditionally — so a path that
        // carried System for its own reasons loses it, and PathHide keeps no record to put it
        // back. Advertising the operation as reversible without saying this was the gap.
        var hideModeHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Localized.SetText(hideModeHint, "settings.hideModeHint");
        hideModeHint[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextSecondaryBrush");

        // Language, display time zone and appearance (theme, then UI font) lead; the Windows-only hide mode follows and
        // shows only where it applies, so the dialog is never cluttered with a setting that does
        // nothing here.
        // Each setting is a section - its heading, its control and its note 8px apart - and the
        // sections sit a wider 20px apart, so the grouping is carried by space.
        var panel = new StackPanel
        {
            Spacing = 20,
            Children =
            {
                Section("settings.language", _languageBox),
                Section("settings.timeZone", _timeZoneBox, timeZoneHint),
                Section("settings.theme", themeRow, themeHint),
                Section("settings.uiFont", _uiFontBox, _uiFontMissing, fontHint),
            },
        };

        if (showWindowsHideMode)
            panel.Children.Add(Section("settings.windowsHideMode", _hiddenAndSystemCheckBox, hideModeHint));

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
        _timeZoneBox.SelectionChanged += (_, _) => UpdateSaveState();
        _uiFontBox.TextChanged += (_, _) =>
        {
            UpdateUiFontMissing();
            UpdateSaveState();
        };
        _hiddenAndSystemCheckBox.IsCheckedChanged += (_, _) => UpdateSaveState();
        foreach (var button in _themeButtons)
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true)
                    KeyboardNavigation.SetTabOnceActiveElement(themeRow, button);
                UpdateSaveState();
            };
        UpdateSaveState();
    }

    // Save commits a draft, so the shell's dirty guard prompts on dismiss and Save stays disabled until
    // the draft actually differs from the saved values: a Save that would change nothing is not
    // offered. Every field is always valid, so dirtiness alone gates the commit.
    protected override bool HasUnsavedChanges =>
        SelectedLanguage != _originalLanguage
        || UiFontFamily != _originalUiFont
        || IsHiddenAndSystem != _originalIsHiddenAndSystem
        || SelectedTheme != _originalTheme
        || SelectedTimeZone != _originalTimeZone;

    protected override async Task<bool> TryCommitAsync(string tag)
    {
        if (tag != "save")
            return true;

        _saveError.IsVisible = false;
        _saveError.Text = string.Empty;
        var language = SelectedLanguage;
        var font = UiFontFamily;
        var hiddenAndSystem = IsHiddenAndSystem;
        var theme = SelectedTheme;
        var timeZone = SelectedTimeZone;
        var failure = await _trySave(language, font, hiddenAndSystem, theme, timeZone);
        if (failure is null)
        {
            _originalLanguage = language;
            _originalUiFont = font;
            _originalIsHiddenAndSystem = hiddenAndSystem;
            _originalTheme = theme;
            _originalTimeZone = timeZone;
            UpdateSaveState();
            return !HasUnsavedChanges;
        }

        _saveError.Text = Localizer.Of(failure);
        _saveError.IsVisible = true;
        return false;
    }

    private void UpdateSaveState() => _saveButton.IsEnabled = HasUnsavedChanges;

    private void OnColorValuesChanged(object? sender, Avalonia.Platform.PlatformColorValues e) => ShowSystemTheme();

    /// <summary>"System (Dark)": the System choice with the computer's appearance now.</summary>
    internal static string SystemThemeLabel(Avalonia.Platform.PlatformThemeVariant appearance) =>
        Localizer.T("settings.themeSystem",
            ("theme", Localizer.T(appearance == Avalonia.Platform.PlatformThemeVariant.Dark ? "settings.themeDark" : "settings.themeLight")));

    private void ShowSystemTheme()
    {
        var appearance = Avalonia.Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant
            ?? Avalonia.Platform.PlatformThemeVariant.Light;
        _themeButtons[0].Content = SystemThemeLabel(appearance);
    }

    private void OnThemeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
            return;
        var step = e.Key switch
        {
            Key.Left or Key.Up => -1,
            Key.Right or Key.Down => 1,
            _ => 0,
        };
        if (step == 0)
            return;
        for (var index = 0; index < _themeButtons.Count; index++)
        {
            if (!_themeButtons[index].IsFocused)
                continue;
            var target = _themeButtons[(index + step + _themeButtons.Count) % _themeButtons.Count];
            target.IsChecked = true;
            target.Focus(NavigationMethod.Directional);
            e.Handled = true;
            return;
        }
    }

    private void UpdateUiFontMissing() => _uiFontMissing.IsVisible = UiFont.FallsBack(_uiFontBox.Text);

    private static StackPanel Section(string headerKey, params Control[] children)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(SectionHeader(headerKey));
        section.Children.AddRange(children);
        return section;
    }

    private static TextBlock SectionHeader(string key)
    {
        var header = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 14 };
        Localized.SetText(header, key);
        return header;
    }
}
