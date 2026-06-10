using System;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests;

public sealed class SessionLogTests
{
    [Fact]
    public void FileName_uses_the_utc_timestamp_filename_convention()
    {
        var name = SessionLog.FileName(
            new DateTimeOffset(2026, 6, 10, 9, 30, 15, 123, TimeSpan.Zero),
            "ABCDEF0123456789ABCDEF0123456789");

        Assert.Equal("20260610-093015-123-utc-abcdef0123456789abcdef0123456789.log", name);
    }

    [Fact]
    public void FileName_converts_a_nonzero_offset_to_utc()
    {
        // 18:30:15.456 +09:00 is the same instant as 09:30:15.456Z, so the name must be
        // the UTC one — proving the stamp is zone-independent, not local.
        var name = SessionLog.FileName(
            new DateTimeOffset(2026, 6, 10, 18, 30, 15, 456, TimeSpan.FromHours(9)),
            "11111111111111111111111111111111");

        Assert.Equal("20260610-093015-456-utc-11111111111111111111111111111111.log", name);
    }

    [Fact]
    public void OpenWriter_creates_a_fresh_file_and_never_appends_to_an_existing_session()
    {
        using var temp = new TempDirectory();
        var timestamp = new DateTimeOffset(2026, 6, 10, 9, 30, 15, 123, TimeSpan.Zero);
        const string sessionId = "22222222222222222222222222222222";
        var path = System.IO.Path.Combine(temp.Path, SessionLog.FileName(timestamp, sessionId));

        using (var writer = SessionLog.OpenWriter(temp.Path, timestamp, sessionId))
        {
            writer.WriteLine("first");
        }

        var ex = Assert.Throws<System.IO.IOException>(() => SessionLog.OpenWriter(temp.Path, timestamp, sessionId));

        Assert.True(System.IO.File.Exists(path));
        Assert.Contains("first", System.IO.File.ReadAllText(path));
        Assert.Contains(SessionLog.FileName(timestamp, sessionId), ex.Message);
    }

    [Fact]
    public void OpenWriter_allows_distinct_sessions_with_the_same_timestamp()
    {
        using var temp = new TempDirectory();
        var timestamp = new DateTimeOffset(2026, 6, 10, 9, 30, 15, 123, TimeSpan.Zero);
        const string firstSession = "33333333333333333333333333333333";
        const string secondSession = "44444444444444444444444444444444";

        using (var writer = SessionLog.OpenWriter(temp.Path, timestamp, firstSession))
            writer.WriteLine("first");

        using (var writer = SessionLog.OpenWriter(temp.Path, timestamp, secondSession))
            writer.WriteLine("second");

        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(temp.Path, SessionLog.FileName(timestamp, firstSession))));
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(temp.Path, SessionLog.FileName(timestamp, secondSession))));
        Assert.Equal(2, System.IO.Directory.GetFiles(temp.Path, "*.log").Length);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pathhide-sessionlog-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Path, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
