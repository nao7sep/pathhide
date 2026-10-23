using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using PathHide.Storage;

namespace PathHide.ViewModels;

/// <summary>
/// The records window's pure decisions: the order it lists records in, where the next page starts, and how a stored value reads on screen.
/// </summary>
internal static class RecordFormat
{
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
    /// A record's fields or error as the plain names and values search matches; a damaged one shows as
    /// stored, so nothing it holds is hidden.
    /// </summary>
    internal static string PlainValues(string text)
    {
        try
        {
            return PathHide.Storage.RecordValues.Plain(text);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>A stored time as the list shows it: the date and time to the second, in the supplied display zone.</summary>
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
