using System.Collections.Generic;
using System.Linq;

namespace PathHide.I18n;

/// <summary>
/// A line in the Settings language list: the value that is saved, and the name shown for it.
///
/// Every language is named in its own words, so a reader finds theirs whatever language is showing;
/// only System is translated, because only System is a sentence about the computer rather than a
/// language's own name.
/// </summary>
public sealed record LanguageOption(string Value, string Name)
{
    /// <summary>System first, then each language by its own name.</summary>
    internal static IReadOnlyList<LanguageOption> All() =>
    [
        new(Languages.System, Localizer.T("settings.languageSystem")),
        .. Languages.All.Select(language => new LanguageOption(language.Tag, language.Name)),
    ];

    /// <summary>The line for a saved preference, falling back to System.</summary>
    internal static LanguageOption For(string? preference, IReadOnlyList<LanguageOption> options)
    {
        var normalized = Languages.NormalizePreference(preference);
        return options.FirstOrDefault(option => option.Value == normalized) ?? options[0];
    }
}
