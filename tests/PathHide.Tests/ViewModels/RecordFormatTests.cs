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
    public void The_next_page_starts_after_the_last_row_shown()
    {
        Assert.Null(RecordFormat.CursorAfter([]));
        var last = Record(3);
        Assert.Equal(new RecordCursor(last.Time, 3), RecordFormat.CursorAfter(Newest(5, 4, 3)));
    }

    // The detail shows plain names and values: a Windows path as typed, nested values indented under
    // their name, list items dashed, and words for true, false and null. Damaged text shows as stored.
    [Fact]
    public void Stored_values_show_plain_and_damaged_text_is_shown_as_it_is()
    {
        var json = """{"path":"C:\\Users\\me\\\"q\".txt","count":3,"ok":true,"none":null,"error":{"type":"IOException"},"tags":["a","b"]}""";

        Assert.Equal(
            "path: C:\\Users\\me\\\"q\".txt\ncount: 3\nok: true\nnone: null\nerror:\n  type: IOException\ntags:\n  - a\n  - b",
            RecordFormat.PlainValues(json));
        Assert.Equal("not json {", RecordFormat.PlainValues("not json {"));
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
