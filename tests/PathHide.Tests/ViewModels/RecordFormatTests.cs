using System;
using System.Globalization;
using System.Linq;
using PathHide.Storage;
using PathHide.ViewModels;
using Xunit;

namespace PathHide.Tests.ViewModels;

public sealed class RecordFormatTests
{
    internal static RecordSummary Record(long id, string session = "s1", string level = "info") =>
        new(id, session, FileTimestamp.SerializedStamp(new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero).AddSeconds(id)),
            level, $"m{id}");

    private static RecordSummary[] Newest(params long[] ids) => ids.Select(id => Record(id)).ToArray();

    [Fact]
    public void New_records_go_ahead_of_the_rows_shown_and_the_pages_already_read_stay()
    {
        var (records, more) = RecordFormat.MergeNewestPage(Newest(5, 4, 3, 2, 1), shownMore: true, new RecordsPage(Newest(7, 6, 5), More: true));

        Assert.Equal([7, 6, 5, 4, 3, 2, 1], records.Select(record => record.Id));
        Assert.True(more);
    }

    [Fact]
    public void The_page_says_whether_more_follow_when_it_reaches_past_every_row_shown()
    {
        var (_, more) = RecordFormat.MergeNewestPage(Newest(2, 1), shownMore: true, new RecordsPage(Newest(3, 2, 1), More: false));

        Assert.False(more);
    }

    [Fact]
    public void An_older_page_arriving_after_a_newer_one_loses_nothing()
    {
        var (records, _) = RecordFormat.MergeNewestPage(Newest(9, 8, 7), shownMore: false, new RecordsPage(Newest(8, 7), More: false));

        Assert.Equal([9, 8, 7], records.Select(record => record.Id));
    }

    [Fact]
    public void An_older_record_handed_back_late_joins_where_its_time_puts_it()
    {
        var (records, _) = RecordFormat.MergeNewestPage(Newest(9, 7), shownMore: false, new RecordsPage(Newest(9, 8, 7), More: false));

        Assert.Equal([9, 8, 7], records.Select(record => record.Id));
    }

    [Fact]
    public void The_next_page_starts_after_the_last_row_shown()
    {
        Assert.Null(RecordFormat.CursorAfter([]));
        var last = Record(3);
        Assert.Equal(new RecordCursor(last.Time, 3), RecordFormat.CursorAfter(Newest(5, 4, 3)));
    }

    [Fact]
    public void Stored_json_is_indented_and_other_text_is_shown_as_it_is()
    {
        Assert.Equal("{\n  \"path\": \"/a/日本\"\n}", RecordFormat.PrettyJson("{\"path\":\"/a/日本\"}").ReplaceLineEndings("\n"));
        Assert.Equal("not json {", RecordFormat.PrettyJson("not json {"));
    }

    [Fact]
    public void A_time_reads_in_the_readers_culture_and_zone_and_the_detail_to_the_millisecond()
    {
        var culture = CultureInfo.GetCultureInfo("de-DE");
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus9", TimeSpan.FromHours(9), "plus9", "plus9");

        Assert.Equal("04.10.2026 18:05:06", RecordFormat.ListTime("2026-10-04T09:05:06.789Z", culture, zone));
        Assert.Equal("04.10.2026 18:05:06,789", RecordFormat.DetailTime("2026-10-04T09:05:06.789Z", culture, zone));
        Assert.Equal("not a time", RecordFormat.ListTime("not a time", culture, zone));
    }
}
