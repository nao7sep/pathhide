using System;
using PathHide.Storage;
using Xunit;

namespace PathHide.Tests.Storage;

/// <summary>
/// The machine-paced UTC filename stamp (<c>yyyyMMdd-HHmmss-fff-utc</c>) used for the quarantine name and
/// the session log. Migrated from the retired backup engine's <c>BackupTime.FileStamp</c> coverage — the
/// only piece of that formatter still in use — after the ZIP engine was removed.
/// </summary>
public sealed class FileTimestampTests
{
    [Fact]
    public void FileStamp_IsUtcMillisecondPrecisionWithSuffix()
    {
        // ms = 7 also pins the zero-padding to three digits ("007"), not a bare "7".
        var value = new DateTimeOffset(2026, 7, 1, 2, 22, 20, 7, TimeSpan.Zero);
        Assert.Equal("20260701-022220-007-utc", FileTimestamp.FileStamp(value));
    }

    [Fact]
    public void FileStamp_ConvertsToUtc()
    {
        // 11:22:20.045 at +09:00 is 02:22:20.045 UTC — the stamp must not carry the local offset.
        var value = new DateTimeOffset(2026, 7, 1, 11, 22, 20, 45, TimeSpan.FromHours(9));
        Assert.Equal("20260701-022220-045-utc", FileTimestamp.FileStamp(value));
    }
}
