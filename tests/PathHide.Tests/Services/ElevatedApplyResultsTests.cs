using System.Linq;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

/// <summary>
/// The JSON-Lines results file is the channel the elevated apply child uses to report each
/// path's outcome back to its unelevated launcher. These cover the round trip and,
/// importantly, that a path the child reports as failed survives parsing — that is the signal
/// the parent's per-row error verdict depends on.
/// </summary>
public sealed class ElevatedApplyResultsTests
{
    [Fact]
    public void Serialize_WritesOneJsonLinePerResult_WithPathAndOkKeys()
    {
        var text = ElevatedApplyResults.Serialize(new[]
        {
            new PathApplyResult(@"C:\a.txt", Ok: true),
            new PathApplyResult(@"C:\b.txt", Ok: false),
        });

        var lines = text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("{\"path\":\"C:\\\\a.txt\",\"ok\":true}", lines[0]);
        Assert.Equal("{\"path\":\"C:\\\\b.txt\",\"ok\":false}", lines[1]);
    }

    [Fact]
    public void RoundTrips_IncludingAFailedPath()
    {
        var original = new[]
        {
            new PathApplyResult(@"C:\Users\First Last\hidden.txt", Ok: true),
            new PathApplyResult(@"C:\protected\denied.txt", Ok: false),
            new PathApplyResult(@"C:\日本語\メモ.txt", Ok: true),
        };

        var parsed = ElevatedApplyResults.Parse(ElevatedApplyResults.Serialize(original));

        Assert.Equal(original, parsed);
        // The failed path must be preserved as a failure — it is how the parent counts an
        // error for a path the elevated child could not change.
        Assert.False(parsed.Single(r => r.Path == @"C:\protected\denied.txt").Ok);
    }

    [Fact]
    public void Serialize_NonAsciiPaths_StayReadable()
    {
        // Relaxed escaping keeps non-ASCII path components legible in the file.
        var text = ElevatedApplyResults.Serialize(new[] { new PathApplyResult(@"C:\日本語\x.txt", Ok: true) });

        Assert.Contains("日本語", text);
    }

    [Fact]
    public void Parse_ToleratesBlankAndMalformedLines()
    {
        var text = "{\"path\":\"C:\\\\good.txt\",\"ok\":true}\n"
                 + "\n"
                 + "not json at all\n"
                 + "{\"path\":\"C:\\\\also-good.txt\",\"ok\":false}\n";

        var parsed = ElevatedApplyResults.Parse(text);

        // The two well-formed lines survive; the blank and garbage lines are skipped rather
        // than discarding the whole (possibly truncated) file.
        Assert.Equal(2, parsed.Count);
        Assert.Equal(@"C:\good.txt", parsed[0].Path);
        Assert.True(parsed[0].Ok);
        Assert.Equal(@"C:\also-good.txt", parsed[1].Path);
        Assert.False(parsed[1].Ok);
    }

    [Fact]
    public void Parse_SkipsLineWithNoPath()
    {
        Assert.Empty(ElevatedApplyResults.Parse("{\"ok\":true}\n"));
    }

    [Fact]
    public void Parse_EmptyText_ReturnsEmpty()
    {
        Assert.Empty(ElevatedApplyResults.Parse(string.Empty));
    }
}
