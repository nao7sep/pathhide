using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PathHide.Text;

/// <summary>
/// The copy of a stored value that a wrapping text block shows. Avalonia lays out one very long line
/// far slower than its length suggests (a 40,000-character Windows path took a second, a 1 MB line
/// minutes), so a line past <see cref="LineLimit"/> graphemes is shortened here, at display time, with
/// one marker (text-cleanup conventions, rule 4). The stored value is never touched: paths.json and
/// the records database keep it whole, and every operation uses it.
/// </summary>
internal static partial class DisplayText
{
    internal const int LineLimit = 10_000;

    /// <summary><paramref name="text"/> with each line longer than <see cref="LineLimit"/> graphemes shortened.</summary>
    public static string CapLines(string text)
    {
        // A line cannot hold more graphemes than the whole text holds UTF-16 units.
        if (text.Length <= LineLimit)
            return text;

        var result = new StringBuilder(text.Length);
        var start = 0;
        foreach (Match separator in LineSeparator().Matches(text))
        {
            result.Append(CapLine(text[start..separator.Index])).Append(separator.Value);
            start = separator.Index + separator.Length;
        }
        return result.Append(CapLine(text[start..])).ToString();
    }

    private static string CapLine(string line)
    {
        if (line.Length <= LineLimit || new StringInfo(line).LengthInTextElements <= LineLimit)
            return line;

        // The indentation stays, so a long line in formatted JSON keeps its place in the structure.
        var indent = line.Length - line.TrimStart().Length;
        var cut = TextCleanup.Truncate(line[indent..], LineLimit);
        return cut.Truncated ? line[..indent] + cut.Text + "…" : line;
    }

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex LineSeparator();
}
