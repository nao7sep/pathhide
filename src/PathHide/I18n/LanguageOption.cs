using System.Collections.Generic;
using System.Linq;

namespace PathHide.I18n;

/// <summary>
/// A line in the Settings language list: the value that is saved, and the name shown for it.
///
/// Every language is named in its own words, so a reader finds theirs whatever language is showing;
/// only System is translated, because only System is a sentence about the computer rather than a
/// language's own name. It names the language it resolves to now, in the interface language, as every
/// System choice does: "System (Japanese)", "システム (日本語)" (localization conventions, Translation).
/// </summary>
public sealed record LanguageOption(string Value, string Name)
{
    /// <summary>
    /// System first, then each language by its own name. <paramref name="systemLanguage"/> is the
    /// language System resolves to, the computer's own by default.
    /// </summary>
    internal static IReadOnlyList<LanguageOption> All(string? systemLanguage = null) =>
    [
        new(Languages.System, Localizer.T("settings.languageSystem",
            ("language", Localizer.T(NameKey(systemLanguage ?? Localizer.SystemLanguage))))),
        .. Languages.All.Select(language => new LanguageOption(language.Tag, language.Name)),
    ];

    /// <summary>The key of a language's name in the interface language, each spelled out whole.</summary>
    internal static string NameKey(string tag) => tag switch
    {
        "de" => "languageName.de",
        "es" => "languageName.es",
        "fr" => "languageName.fr",
        "it" => "languageName.it",
        "ja" => "languageName.ja",
        "ko" => "languageName.ko",
        "pt-BR" => "languageName.pt-BR",
        "ru" => "languageName.ru",
        "zh-Hans" => "languageName.zh-Hans",
        _ => "languageName.en",
    };

    /// <summary>The line for a saved preference, falling back to System.</summary>
    internal static LanguageOption For(string? preference, IReadOnlyList<LanguageOption> options)
    {
        var normalized = Languages.NormalizePreference(preference);
        return options.FirstOrDefault(option => option.Value == normalized) ?? options[0];
    }
}
