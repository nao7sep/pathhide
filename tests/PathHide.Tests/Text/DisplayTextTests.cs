using System.Globalization;
using System.Linq;
using PathHide.Text;
using Xunit;

namespace PathHide.Tests.Text;

public sealed class TextCleanupTests
{
    // The truncation cases the text-cleanup conventions ask each app's copy to pin.
    [Fact]
    public void Truncate_keeps_at_least_n_graphemes_and_reports_a_cut()
    {
        Assert.Equal(new TextCleanup.TruncateResult("abc", true), TextCleanup.Truncate("abcdef", 3));
        Assert.Equal(new TextCleanup.TruncateResult("abc", false), TextCleanup.Truncate("abc", 3));
        Assert.Equal(new TextCleanup.TruncateResult("", false), TextCleanup.Truncate("abc", 0));
    }

    [Fact]
    public void Truncate_reports_no_cut_for_an_all_whitespace_tail()
    {
        Assert.Equal(new TextCleanup.TruncateResult("abc", false), TextCleanup.Truncate("abc   \n  ", 3));
    }

    [Fact]
    public void Truncate_never_splits_a_grapheme()
    {
        var family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        var result = TextCleanup.Truncate("a" + family + "b", 2);
        Assert.Equal("a" + family, result.Text);
        Assert.True(result.Truncated);
    }
}

public sealed class DisplayTextTests
{
    private const int Limit = DisplayText.LineLimit;

    [Fact]
    public void Text_within_the_limit_is_returned_as_it_is()
    {
        var text = "  two  spaces\r\nkept\n";
        Assert.Same(text, DisplayText.CapLines(text));
        var exactly = new string('a', Limit);
        Assert.Same(exactly, DisplayText.CapLines(exactly));
    }

    [Fact]
    public void Only_a_line_past_the_limit_is_shortened_with_one_marker()
    {
        var longLine = @"C:" + string.Concat(Enumerable.Repeat(@"\segment", Limit / 4));
        var text = "first\r\n    " + longLine + "\rlast\n";

        var shown = DisplayText.CapLines(text);
        var lines = shown.Split(["\r\n", "\r", "\n"], System.StringSplitOptions.None);

        Assert.Equal(["first", "last", ""], new[] { lines[0], lines[2], lines[3] });
        Assert.StartsWith("    C:\\segment", lines[1]);
        Assert.EndsWith("…", lines[1]);
        Assert.Equal(1, lines[1].Count(c => c == '…'));
        Assert.Equal(4 + Limit + 1, new StringInfo(lines[1]).LengthInTextElements);
        // Every separator keeps its own form, including a lone carriage return.
        Assert.Equal("first\r\n", shown[..7]);
        Assert.EndsWith("…\rlast\n", shown);
    }

    [Fact]
    public void A_line_counts_graphemes_not_code_units()
    {
        // Past the limit in UTF-16 units, within it in graphemes: nothing is cut.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", Limit));
        Assert.Equal(emoji, DisplayText.CapLines(emoji));
    }
}

public sealed class RecordDisplayTests
{
    [Fact]
    public void A_records_overlong_lines_are_shortened_for_display_and_the_record_keeps_them_whole()
    {
        var huge = new string('x', PathHide.Text.DisplayText.LineLimit * 3);
        var fields = "{\"path\":\"" + huge + "\",\"count\":2}";
        var detail = new PathHide.Storage.RecordDetail(1, "s", "2026-10-13T00:00:00.000Z", "error", huge, fields, null);
        var view = new PathHide.ViewModels.RecordDetailViewModel(detail, "s");
        var row = new PathHide.ViewModels.RecordRowViewModel(
            new PathHide.Storage.RecordSummary(1, "s", detail.Time, "error", huge));

        Assert.Equal(huge, detail.Message);
        Assert.Equal(fields, detail.Fields);
        Assert.Equal(PathHide.Text.DisplayText.LineLimit + 1, view.Message.Length);
        Assert.Equal(view.Message, row.Message);
        var fieldLines = view.FieldsText!.Split('\n');
        var pathLine = Assert.Single(fieldLines, line => line.Contains("\"path\""));
        Assert.EndsWith("…", pathLine);
        Assert.Contains(fieldLines, line => line.Contains("\"count\": 2"));
        Assert.All(fieldLines, line => Assert.True(line.Length <= PathHide.Text.DisplayText.LineLimit + 16));
    }
}
