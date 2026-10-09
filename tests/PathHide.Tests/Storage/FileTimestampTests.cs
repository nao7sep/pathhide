using System;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>
/// The UTC filename stamp (<c>yyyyMMdd-HHmmss-utc</c>) a session's fallback log file is named with.
/// </summary>
public sealed class FileTimestampTests
{
    [Fact]
    public void FileStamp_IsUtcSecondPrecisionWithSuffix()
    {
        // The milliseconds are dropped: two sessions starting in the same second share one file.
        var value = new DateTimeOffset(2026, 7, 1, 2, 22, 20, 7, TimeSpan.Zero);
        Assert.Equal("20260701-022220-utc", FileTimestamp.FileStamp(value));
    }

    [Fact]
    public void FileStamp_ConvertsToUtc()
    {
        // 11:22:20.045 at +09:00 is 02:22:20.045 UTC — the stamp must not carry the local offset.
        var value = new DateTimeOffset(2026, 7, 1, 11, 22, 20, 45, TimeSpan.FromHours(9));
        Assert.Equal("20260701-022220-utc", FileTimestamp.FileStamp(value));
    }
}
