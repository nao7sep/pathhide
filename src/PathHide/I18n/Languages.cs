using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PathHide.I18n;

/// <summary>
/// The interface languages PathHide speaks (localization conventions): the ten tags, the name each
/// one is shown under, how a saved preference is read, and how <see cref="System"/> resolves against
/// the computer's own languages.
///
/// A tag is BCP 47 and never shown to the user; a name is written in its own language so a reader
/// finds it whatever language is currently showing.
/// </summary>
internal static class Languages
{
    /// <summary>The saved value that means "follow the computer".</summary>
    internal const string System = "system";

    /// <summary>The source language, and the fallback for a computer outside the set.</summary>
    internal const string English = "en";

    /// <summary>
    /// The set, in the picker's order: the Latin-script languages alphabetically by their own names,
    /// then Cyrillic, then Chinese, Japanese and Korean.
    /// </summary>
    internal static IReadOnlyList<(string Tag, string Name)> All { get; } =
    [
        ("de", "Deutsch"),
        ("en", "English"),
        ("es", "Español"),
        ("fr", "Français"),
        ("it", "Italiano"),
        ("pt-BR", "Português"),
        ("ru", "Русский"),
        ("zh-Hans", "中文"),
        ("ja", "日本語"),
        ("ko", "한국어"),
    ];

    internal static IReadOnlyList<string> Tags { get; } = All.Select(language => language.Tag).ToArray();

    /// <summary>The name <paramref name="tag"/> is shown under, or the tag itself if it is not in the set.</summary>
    internal static string NameOf(string tag) =>
        All.FirstOrDefault(language => language.Tag == tag).Name ?? tag;

    /// <summary>
    /// A saved preference as the app should act on it: a tag in the set, or <see cref="System"/> for a
    /// missing, blank or unrecognized value, so a hand-edited file can never leave the app without a
    /// language.
    /// </summary>
    internal static string NormalizePreference(string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved))
            return System;
        var trimmed = saved.Trim();
        if (string.Equals(trimmed, System, StringComparison.OrdinalIgnoreCase))
            return System;
        return Tags.FirstOrDefault(tag => string.Equals(tag, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? System;
    }

    /// <summary>
    /// The language a preference resolves to, given the computer's preferred languages in order.
    /// <see cref="System"/> takes the first of those in the set, and English when none is.
    /// </summary>
    internal static string Resolve(string? preference, IReadOnlyList<string> computerLanguages)
    {
        var normalized = NormalizePreference(preference);
        return normalized == System ? Match(computerLanguages) : normalized;
    }

    /// <summary>
    /// The first of <paramref name="computerLanguages"/> that is in the set, and English when none is.
    /// Every Chinese locale resolves to Simplified Chinese, every Portuguese to Brazilian Portuguese,
    /// and every Spanish to the one neutral Spanish, because each app ships only that one variety.
    /// </summary>
    internal static string Match(IReadOnlyList<string> computerLanguages)
    {
        foreach (var candidate in computerLanguages)
        {
            if (MatchOne(candidate) is { } tag)
                return tag;
        }

        return English;
    }

    private static string? MatchOne(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        // "ja-JP", "zh-Hant-TW" and "pt_BR" all arrive here; only the primary language subtag, and for
        // Chinese the script, decide.
        var parts = candidate.Trim().Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        return parts[0].ToLowerInvariant() switch
        {
            "zh" => "zh-Hans",
            "pt" => "pt-BR",
            "es" => "es",
            "de" => "de",
            "en" => "en",
            "fr" => "fr",
            "it" => "it",
            "ru" => "ru",
            "ja" => "ja",
            "ko" => "ko",
            _ => null,
        };
    }

    /// <summary>
    /// The culture dates and numbers are formatted in (timestamp conventions): the computer's own
    /// regional format when the computer already works in the interface language, so a reader keeps
    /// the date order and separators they set, and otherwise the language's own.
    /// </summary>
    internal static CultureInfo FormattingCulture(string tag, CultureInfo computerCulture)
    {
        if (MatchOne(computerCulture.Name) == tag)
            return computerCulture;

        try
        {
            return CultureInfo.GetCultureInfo(tag);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo(English);
        }
    }
}
