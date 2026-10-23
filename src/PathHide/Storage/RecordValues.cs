using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PathHide.Storage;

/// <summary>
/// A record's fields or error as plain names and values: strings decoded, numbers as written, and
/// <c>true</c>, <c>false</c> and <c>null</c> as words. The detail pane shows exactly these, and search
/// matches exactly these, so a value copied from the detail finds its record.
/// </summary>
internal static class RecordValues
{
    /// <summary>One name, one value, or a name that opens nested values, at its depth.</summary>
    internal readonly record struct Entry(int Depth, string? Name, string? Value);

    /// <summary>The entries of a record's JSON. Throws <see cref="JsonException"/> when it is damaged.</summary>
    internal static IReadOnlyList<Entry> Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var entries = new List<Entry>();
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
                Walk(property.Value, 0, property.Name, entries);
        }
        else
        {
            Walk(document.RootElement, 0, null, entries);
        }
        return entries;
    }

    /// <summary>
    /// The entries as the detail pane shows them, one per line, nested values indented under their
    /// name and list items marked with a dash.
    /// </summary>
    internal static string Plain(string json)
    {
        var text = new StringBuilder();
        foreach (var entry in Read(json))
        {
            if (text.Length > 0)
                text.Append('\n');
            text.Append(' ', entry.Depth * 2);
            text.Append(entry.Name is null ? "-" : entry.Name + ":");
            if (entry.Value is not null)
                text.Append(' ').Append(entry.Value);
        }
        return text.ToString();
    }

    private static void Walk(JsonElement element, int depth, string? name, List<Entry> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                into.Add(new Entry(depth, name, null));
                foreach (var property in element.EnumerateObject())
                    Walk(property.Value, depth + 1, property.Name, into);
                break;
            case JsonValueKind.Array:
                into.Add(new Entry(depth, name, null));
                foreach (var item in element.EnumerateArray())
                    Walk(item, depth + 1, null, into);
                break;
            default:
                into.Add(new Entry(depth, name, Scalar(element)));
                break;
        }
    }

    private static string Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => element.GetRawText(),
    };
}
