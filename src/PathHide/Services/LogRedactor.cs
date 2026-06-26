using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace PathHide.Services;

/// <summary>
/// The mandatory, deliberately narrow redaction backstop from the logging
/// conventions. It exists for the day someone logs a whole object that happens to
/// contain a secret; the primary defense is still "summarize, don't dump."
/// </summary>
/// <remarks>
/// The contract — guaranteed by construction, so the redactor can never corrupt a
/// log line:
/// <list type="bullet">
/// <item>Runs on the structured node tree <em>before</em> serialization — it never
/// sees message prose.</item>
/// <item>Matches field names by <em>exact, case-insensitive</em> name only — never
/// by substring, so <c>token</c> never matches <c>tokenCount</c> or <c>broken</c>.</item>
/// <item>Replaces only the <em>value</em> of a matched field with the fixed marker
/// (<see cref="Marker"/>); every other field stays byte-identical.</item>
/// <item>Recurses through nested objects and arrays.</item>
/// <item>Never regex-scans string values.</item>
/// <item>Pure and total: it mutates the caller-owned tree in place, cannot throw,
/// cannot drop fields, and always leaves valid JSON.</item>
/// </list>
/// </remarks>
public static class LogRedactor
{
    /// <summary>The fixed marker substituted for a redacted value.</summary>
    public const string Marker = "[redacted]";

    /// <summary>
    /// Redacts <paramref name="node"/> in place against <paramref name="deniedKeys"/>.
    /// The set is expected to use a case-insensitive comparer. A null node is a no-op.
    /// </summary>
    public static void Redact(JsonNode? node, IReadOnlySet<string> deniedKeys)
    {
        switch (node)
        {
            case JsonObject obj:
                RedactObject(obj, deniedKeys);
                break;
            case JsonArray array:
                foreach (var item in array)
                    Redact(item, deniedKeys);
                break;
        }
    }

    private static void RedactObject(JsonObject obj, IReadOnlySet<string> deniedKeys)
    {
        // Snapshot the names first: we reassign values below, and mutating a
        // JsonObject while enumerating it is not allowed.
        foreach (var name in obj.Select(pair => pair.Key).ToList())
        {
            if (deniedKeys.Contains(name))
                obj[name] = Marker;            // replace the value, keep the key
            else
                Redact(obj[name], deniedKeys); // recurse into nested structure
        }
    }
}
