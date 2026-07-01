using System;
using PathHide.Backup;
using Xunit;

namespace PathHide.Tests.Backup;

/// <summary>
/// UTC timestamp formatting: the archive stem is the <c>yyyyMMdd-HHmmss-utc</c> file stamp, and the index
/// stores whole-second ISO-8601 UTC values that round-trip back through the parser.
/// </summary>
public sealed class BackupTimeTests
{
    [Fact]
    public void FileStamp_IsUtcSecondPrecisionWithSuffix()
    {
        var value = new DateTimeOffset(2026, 7, 1, 2, 22, 20, TimeSpan.Zero);
        Assert.Equal("20260701-022220-utc", BackupTime.FileStamp(value));
    }

    [Fact]
    public void FileStamp_ConvertsToUtc()
    {
        // 11:22:20 at +09:00 is 02:22:20 UTC — the stamp must not carry the local offset.
        var value = new DateTimeOffset(2026, 7, 1, 11, 22, 20, TimeSpan.FromHours(9));
        Assert.Equal("20260701-022220-utc", BackupTime.FileStamp(value));
    }

    [Fact]
    public void ToIsoSeconds_DropsSubSecondAndUsesZSuffix()
    {
        var value = new DateTimeOffset(2026, 7, 1, 2, 22, 20, 483, TimeSpan.Zero);
        Assert.Equal("2026-07-01T02:22:20Z", BackupTime.ToIsoSeconds(value));
    }

    [Fact]
    public void TryParseIso_RoundTripsWholeSecondStamp()
    {
        var value = new DateTimeOffset(2026, 7, 1, 2, 22, 20, TimeSpan.Zero);
        var text = BackupTime.ToIsoSeconds(value);

        Assert.True(BackupTime.TryParseIso(text, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Fact]
    public void TryParseIso_RejectsGarbage()
    {
        Assert.False(BackupTime.TryParseIso("not a timestamp", out _));
    }
}
