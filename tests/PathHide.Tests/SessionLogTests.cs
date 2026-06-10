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
            new DateTimeOffset(2026, 6, 10, 9, 30, 15, 123, TimeSpan.Zero));

        Assert.Equal("20260610-093015-utc.log", name);
    }

    [Fact]
    public void FileName_converts_a_nonzero_offset_to_utc()
    {
        // 18:30:15 +09:00 is the same instant as 09:30:15Z, so the name must be the
        // UTC one — proving the stamp is zone-independent, not local.
        var name = SessionLog.FileName(
            new DateTimeOffset(2026, 6, 10, 18, 30, 15, 456, TimeSpan.FromHours(9)));

        Assert.Equal("20260610-093015-utc.log", name);
    }

    [Fact]
    public void OpenWriter_creates_a_fresh_file_and_never_appends_to_an_existing_session()
    {
        using var temp = new TempDirectory();
        var timestamp = new DateTimeOffset(2026, 6, 10, 9, 30, 15, 123, TimeSpan.Zero);
        var path = System.IO.Path.Combine(temp.Path, SessionLog.FileName(timestamp));

        using (var writer = SessionLog.OpenWriter(temp.Path, timestamp))
        {
            writer.WriteLine("first");
        }

        // A second launch resolving to the same UTC second collides on the name;
        // the exclusive create throws rather than appending into the first session.
        // (Log.Start catches this and degrades to console logging.)
        var ex = Assert.Throws<System.IO.IOException>(() => SessionLog.OpenWriter(temp.Path, timestamp));

        Assert.True(System.IO.File.Exists(path));
        Assert.Contains("first", System.IO.File.ReadAllText(path));
        Assert.Contains(SessionLog.FileName(timestamp), ex.Message);
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
