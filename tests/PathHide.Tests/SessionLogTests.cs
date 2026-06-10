using System;
using PathHide.Services;
using Xunit;

namespace PathHide.Tests;

public sealed class SessionLogTests
{
    [Fact]
    public void FileName_uses_the_utc_timestamp_filename_convention()
    {
        var name = SessionLog.FileName(new DateTimeOffset(2026, 6, 10, 9, 30, 15, TimeSpan.Zero));
        Assert.Equal("20260610-093015-utc.log", name);
    }

    [Fact]
    public void FileName_converts_a_nonzero_offset_to_utc()
    {
        // 18:30:15 +09:00 is the same instant as 09:30:15Z, so the name must be
        // the UTC one — proving the stamp is zone-independent, not local.
        var name = SessionLog.FileName(new DateTimeOffset(2026, 6, 10, 18, 30, 15, TimeSpan.FromHours(9)));
        Assert.Equal("20260610-093015-utc.log", name);
    }
}
