using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PathHide.Services;

/// <summary>One path's outcome from the elevated apply pass: the exact path the elevated
/// child was handed, and whether it set the attribute successfully.</summary>
public sealed record PathApplyResult(string Path, bool Ok);

/// <summary>
/// The wire format for the elevated child's per-path results file. The elevated
/// <c>apply</c> process cannot stream stdout back to its launcher (the <c>runas</c> verb
/// forces <c>UseShellExecute = true</c>), so it writes one result per path to a temp file
/// the unelevated parent then reads. The format is JSON Lines to match the app's logging
/// style — one <c>{ "path": ..., "ok": ... }</c> object per line.
/// </summary>
/// <remarks>
/// Both sides of a trust-and-privilege boundary share this format, so it lives in one
/// place. <see cref="Parse"/> is deliberately tolerant: a truncated or partly garbled file
/// (e.g. an elevated child that crashed mid-write) still yields every well-formed line
/// rather than being discarded whole.
/// </remarks>
public static class ElevatedApplyResults
{
    // One physical line per result; camelCase keys ("path"/"ok"); relaxed escaping so a
    // non-ASCII path component (e.g. Japanese) stays readable — mirroring SessionLogger.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(IEnumerable<PathApplyResult> results)
    {
        var builder = new StringBuilder();
        foreach (var result in results)
            builder.Append(JsonSerializer.Serialize(result, Options)).Append('\n');
        return builder.ToString();
    }

    public static IReadOnlyList<PathApplyResult> Parse(string text)
    {
        var results = new List<PathApplyResult>();
        if (string.IsNullOrEmpty(text))
            return results;

        // Split on '\n' and Trim, so a trailing '\r' (CRLF) or a blank line is ignored.
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            try
            {
                var result = JsonSerializer.Deserialize<PathApplyResult>(line, Options);
                // A line with no usable path carries no outcome to map, so skip it rather
                // than recording a result keyed on an empty path.
                if (result is not null && !string.IsNullOrEmpty(result.Path))
                    results.Add(result);
            }
            catch (JsonException)
            {
                // Tolerate one malformed line; the rest of the file is still usable.
            }
        }

        return results;
    }
}
