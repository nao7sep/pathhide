using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PathHide.Storage;

namespace PathHide.ViewModels;

/// <summary>
/// The records window's pure decisions: the order it lists records in, how a newest page joins the rows
/// shown, where the next page starts, and how a stored value reads on screen.
/// </summary>
internal static class RecordFormat
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The order the list shows records in, newest first; the database pages them the same way.</summary>
    internal static int NewestFirst(RecordSummary a, RecordSummary b)
    {
        var byTime = string.CompareOrdinal(b.Time, a.Time);
        return byTime != 0 ? byTime : b.Id.CompareTo(a.Id);
    }

    /// <summary>The page after the last record shown, or the first page when none is.</summary>
    internal static RecordCursor? CursorAfter(IReadOnlyList<RecordSummary> shown) =>
        shown.Count == 0 ? null : new RecordCursor(shown[^1].Time, shown[^1].Id);

    /// <summary>
    /// The newest page read again, joined with the rows already shown: a row in both stays as shown,
    /// and the rows shown beyond the page stay, so the pages already read are kept and a page read out
    /// of order loses nothing. Whether more follow is the page's word only when it reaches past every
    /// row shown.
    /// </summary>
    internal static (IReadOnlyList<RecordSummary> Records, bool More) MergeNewestPage(
        IReadOnlyList<RecordSummary> shown, bool shownMore, RecordsPage page)
    {
        var byId = shown.ToDictionary(record => record.Id);
        foreach (var record in page.Records)
            byId.TryAdd(record.Id, record);

        var records = byId.Values.ToList();
        records.Sort(NewestFirst);
        var beyond = page.Records.Count > 0 && shown.Any(record => NewestFirst(record, page.Records[^1]) > 0);
        return (records, beyond ? shownMore : page.More);
    }

    /// <summary>Stored JSON, indented for reading; text that is not JSON is shown as it is.</summary>
    internal static string PrettyJson(string text)
    {
        try
        {
            return JsonNode.Parse(text)?.ToJsonString(Indented) ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>A stored time as the list shows it: the date and time to the second, in the computer's zone.</summary>
    internal static string ListTime(string stored, CultureInfo culture, TimeZoneInfo zone) =>
        Parse(stored, zone) is { } time ? time.ToString("G", culture) : stored;

    /// <summary>A stored time as the detail shows it: the list's form, to the millisecond.</summary>
    internal static string DetailTime(string stored, CultureInfo culture, TimeZoneInfo zone)
    {
        if (Parse(stored, zone) is not { } time)
            return stored;

        var format = culture.DateTimeFormat;
        var seconds = format.LongTimePattern.Contains("ss", StringComparison.Ordinal)
            ? format.LongTimePattern.Replace("ss", "ss'" + culture.NumberFormat.NumberDecimalSeparator + "'fff", StringComparison.Ordinal)
            : format.LongTimePattern;
        return time.ToString(format.ShortDatePattern + " " + seconds, culture);
    }

    // A stored time is the serialized UTC form; anything else is shown as it was stored.
    private static DateTimeOffset? Parse(string stored, TimeZoneInfo zone) =>
        DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? TimeZoneInfo.ConvertTime(time, zone)
            : null;
}
