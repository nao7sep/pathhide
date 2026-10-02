using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PathHide.Services;

/// <summary>One path's outcome from the elevated apply pass: the exact path the elevated
/// child was handed, and whether it set the attribute successfully.</summary>
public sealed record PathApplyResult(string Path, bool Ok);

/// <summary>What the elevated child handed back: its per-path outcomes and its log entries.</summary>
public sealed record ElevatedApplyReport(
    IReadOnlyList<PathApplyResult> Results,
    IReadOnlyList<LogEntry> Entries);

/// <summary>
/// The wire format for the elevated child's results file. The elevated <c>apply</c> process cannot
/// stream stdout back to its launcher (the <c>runas</c> verb forces <c>UseShellExecute = true</c>), so it
/// writes one line per path, and one per log entry, to a temp file the unelevated parent then reads.
/// The format is JSON Lines: a result is <c>{ "path": ..., "ok": ... }</c>, a log entry is
/// <c>{ "entry": { ... } }</c>.
/// </summary>
/// <remarks>
/// Both sides of a trust-and-privilege boundary share this format, so it lives in one
/// place. <see cref="Parse"/> is deliberately tolerant: a truncated or partly garbled file
/// (e.g. an elevated child that crashed mid-write) still yields every well-formed line
/// rather than being discarded whole.
/// </remarks>
public static class ElevatedApplyResults
{
    private const string EntryKey = "entry";

    // One physical line per result; camelCase keys ("path"/"ok"); relaxed escaping so a
    // non-ASCII path component (e.g. Japanese) stays readable, as in a log entry.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One JSONL record, newline-terminated. Appending these one at a time is what
    /// makes a partial file meaningful when the child is killed or stalls mid-run.</summary>
    public static string SerializeLine(PathApplyResult result) =>
        JsonSerializer.Serialize(result, Options) + "\n";

    /// <summary>One log entry's line, newline-terminated.</summary>
    public static string SerializeLine(LogEntry entry) =>
        "{\"" + EntryKey + "\":" + entry.ToLine() + "}\n";

    public static string Serialize(IEnumerable<PathApplyResult> results)
    {
        var builder = new StringBuilder();
        foreach (var result in results)
            builder.Append(SerializeLine(result));
        return builder.ToString();
    }

    public static ElevatedApplyReport Parse(string text)
    {
        var results = new List<PathApplyResult>();
        var entries = new List<LogEntry>();
        if (string.IsNullOrEmpty(text))
            return new ElevatedApplyReport(results, entries);

        // Split on '\n' and Trim, so a trailing '\r' (CRLF) or a blank line is ignored.
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            try
            {
                if (JsonNode.Parse(line) is not JsonObject node)
                    throw new JsonException("The line is not a JSON object.");

                if (node[EntryKey] is { } entryNode)
                {
                    if (LogEntry.FromNode(entryNode) is { } entry)
                        entries.Add(entry);
                    continue;
                }

                var result = node.Deserialize<PathApplyResult>(Options);
                // A line with no usable path carries no outcome to map, so skip it rather
                // than recording a result keyed on an empty path.
                if (result is not null && !string.IsNullOrEmpty(result.Path))
                    results.Add(result);
            }
            catch (JsonException ex)
            {
                // Tolerate one malformed line; the rest of the file is still usable. The child is
                // our own code writing well-formed JSONL, so a malformed line means it was cut off
                // — killed mid-write, or a full disk. Without this line the parent silently
                // under-counts successes and the log shows only a short reported count with no
                // explanation for it.
                Log.Warn("elevated apply: skipping a malformed line in the results file", ex);
            }
        }

        return new ElevatedApplyReport(results, entries);
    }
}

/// <summary>
/// The elevated child's side of the results file: it creates the file, appends each path's outcome and
/// each log entry as its own line, and flushes every line, so a partial file is a true running record.
/// </summary>
/// <remarks>
/// The file is created here, never reused (a pre-existing file at this name is refused rather than
/// written through), and held open without delete sharing for the whole run, which is what tells the
/// parent's cleanup that this child is still running.
/// </remarks>
public sealed class ElevatedResultsWriter : ILogSink
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    private ElevatedResultsWriter(StreamWriter writer) => _writer = writer;

    public static ElevatedResultsWriter Create(string path) =>
        new(new StreamWriter(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));

    public void Report(PathApplyResult result) => Append(ElevatedApplyResults.SerializeLine(result));

    public void Write(LogEntry entry) => Append(ElevatedApplyResults.SerializeLine(entry));

    public void Dispose()
    {
        lock (_gate)
            _writer.Dispose();
    }

    private void Append(string line)
    {
        lock (_gate)
        {
            // not recorded: a transient elevated-IPC file in the OS temp directory, never reloaded as
            // managed state.
            _writer.Write(line);
            _writer.Flush();
        }
    }
}
