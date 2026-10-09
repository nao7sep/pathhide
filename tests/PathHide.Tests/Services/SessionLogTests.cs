using System;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests.Services;

public sealed class SessionLogTests
{
    [Fact]
    public void FileName_uses_the_utc_timestamp_filename_convention()
    {
        var name = SessionLog.FileName(
            new DateTimeOffset(2026, 6, 10, 9, 30, 15, 123, TimeSpan.Zero));

        Assert.Equal("20260610-093015-utc.log", name);
    }

    [Fact]
    public void FileName_converts_a_nonzero_offset_to_utc()
    {
        // 18:30:15.456 +09:00 is the same instant as 09:30:15.456Z, so the name must be
        // the UTC one — proving the stamp is zone-independent, not local.
        var name = SessionLog.FileName(
            new DateTimeOffset(2026, 6, 10, 18, 30, 15, 456, TimeSpan.FromHours(9)));

        Assert.Equal("20260610-093015-utc.log", name);
    }

    [Fact]
    public void Append_adds_each_line_to_the_sessions_one_file()
    {
        using var temp = new TempDirectory();
        var sessionStart = new DateTimeOffset(2026, 6, 10, 9, 30, 15, 123, TimeSpan.Zero);
        var logs = System.IO.Path.Combine(temp.Path, "logs");

        SessionLog.Append(logs, sessionStart, "first");
        SessionLog.Append(logs, sessionStart, "second");

        var file = Assert.Single(System.IO.Directory.GetFiles(logs));
        Assert.Equal(SessionLog.FileName(sessionStart), System.IO.Path.GetFileName(file));
        Assert.Equal(["first", "second"], System.IO.File.ReadAllLines(file));
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
