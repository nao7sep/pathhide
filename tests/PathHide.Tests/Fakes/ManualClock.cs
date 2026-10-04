using System;

namespace PathHide.Tests.Fakes;

/// <summary>A <see cref="TimeProvider"/> whose time moves only when a test advances it.</summary>
public sealed class ManualClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
