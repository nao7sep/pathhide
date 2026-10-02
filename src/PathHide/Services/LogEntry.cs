using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PathHide.Services;

/// <summary>
/// One log record (logging and data-lifecycle conventions): the session it came from, the event's
/// time, level and message, the caller's free fields as given, and the full exception when there was
/// one. <see cref="Session"/> is the serialized start time of the process launch that logged it, so an
/// entry handed back by the elevated child keeps the child's own session.
/// </summary>
public sealed record LogEntry(
    string Session,
    string Time,
    string Level,
    string Message,
    JsonObject? Fields,
    JsonObject? Error)
{
    // One physical line per entry. Relaxed escaping keeps non-ASCII (e.g. Japanese path components)
    // readable; newlines inside string values are still escaped.
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The entry as one JSON object on one line, for a text file or the console.</summary>
    public string ToLine() => JsonSerializer.Serialize(this, LineOptions);

    /// <summary>Reads <see cref="ToLine"/>'s form back; null when it is not an entry.</summary>
    public static LogEntry? FromNode(JsonNode node)
    {
        var entry = node.Deserialize<LogEntry>(LineOptions);
        return entry is { Session.Length: > 0, Time.Length: > 0, Level.Length: > 0, Message: not null }
            ? entry
            : null;
    }

    /// <summary>Serializes a node for a text column.</summary>
    public static string? ToText(JsonNode? node) => node?.ToJsonString(LineOptions);
}
