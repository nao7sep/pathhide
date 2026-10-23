using System;
using System.Collections.Generic;
using static PathHide.I18n.Plural;

namespace PathHide.Tests.I18n;

internal static class PluralExpectations
{
    /// <summary>
    /// The forms a language's entry must have, and no others. The catalogue gate holds every plural
    /// entry to exactly this set, so a translator can neither leave a form out nor invent one.
    /// </summary>
    internal static IReadOnlyList<string> CategoriesOf(string tag) => tag switch
    {
        // One form: these languages do not change the noun with the number.
        "ja" or "ko" or "zh-Hans" => [Other],

        // Singular and plural.
        "en" or "de" => [One, Other],

        // A separate form for large round numbers in these Romance languages.
        "es" or "fr" or "it" or "pt-BR" => [One, Many, Other],

        // Russian changes the noun at 1, at 2 to 4, and again above.
        "ru" => [One, Few, Many, Other],

        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "Not an interface language."),
    };

}
