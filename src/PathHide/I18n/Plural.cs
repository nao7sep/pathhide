using System;

namespace PathHide.I18n;

/// <summary>
/// Which plural form a count takes in each language, and which forms a catalogue entry must carry.
///
/// .NET exposes no plural rules of its own — <c>System.Globalization</c> formats numbers but never
/// says which words go with them — so the ten languages' CLDR cardinal rules are written out here.
/// Counts in the interface are whole numbers, so the rules are given for integers: every operand but
/// <c>n</c> and <c>i</c> is then fixed, which is what removes CLDR's compound conditions. A count that
/// is genuinely fractional would need the rules restated with the decimal operands.
/// </summary>
internal static class Plural
{
    internal const string One = "one";
    internal const string Few = "few";
    internal const string Many = "many";
    internal const string Other = "other";

    /// <summary>The form <paramref name="count"/> takes in <paramref name="tag"/>.</summary>
    internal static string CategoryFor(string tag, long count)
    {
        var n = Math.Abs(count);
        return tag switch
        {
            "ja" or "ko" or "zh-Hans" => Other,

            "en" or "de" or "it" or "es" => n == 1 ? One : Romance(tag, n),

            // French and Brazilian Portuguese count zero with the singular.
            "fr" or "pt-BR" => n is 0 or 1 ? One : Romance(tag, n),

            "ru" => n % 10 == 1 && n % 100 != 11 ? One
                : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? Few
                : Many,

            _ => Other,
        };
    }

    // The Romance "many" form is for round millions; English and German have no such form.
    private static string Romance(string tag, long n) =>
        tag is "en" or "de" ? Other
        : n != 0 && n % 1_000_000 == 0 ? Many
        : Other;
}
