using System.Globalization;
using System.Text;

namespace PathHide.Text;

/// <summary>
/// The multiline-truncation pattern of the text-cleanup conventions, copied from the canonical C#
/// reference (company/reference/text-cleanup/TextCleanup.cs). Keep it identical to the reference; a
/// fix belongs there first.
/// </summary>
internal static class TextCleanup
{
    public readonly record struct TruncateResult(string Text, bool Truncated);

    /// <summary>
    /// At least <paramref name="n"/> graphemes of <paramref name="text"/> on one line, whitespace runs
    /// collapsed to one space, and whether a visible grapheme was left out. Appends no marker.
    /// </summary>
    public static TruncateResult Truncate(string text, int n)
    {
        if (n <= 0) return new TruncateResult("", false);
        var sb = new StringBuilder();
        int count = 0; // graphemes appended, including inserted spaces
        bool pendingSpace = false, budgetMet = false, truncated = false;

        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            string g = (string)e.Current;
            bool isWs = string.IsNullOrWhiteSpace(g);
            if (!budgetMet)
            {
                if (isWs)
                {
                    if (count > 0) pendingSpace = true;
                    continue;
                }
                if (pendingSpace) { sb.Append(' '); count++; pendingSpace = false; }
                sb.Append(g); count++;
                if (count >= n) budgetMet = true;
            }
            else if (!isWs)
            {
                truncated = true;
                break;
            }
        }
        return new TruncateResult(sb.ToString(), truncated);
    }
}
