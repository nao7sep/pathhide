using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PathHide.Tests.Fakes;

/// <summary>
/// A <see cref="TimeProvider"/> whose time moves only when a test advances it. Its timers fire only as
/// <see cref="Advance"/> reaches them, so a timeout runs out exactly when a test says so.
/// </summary>
public sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    /// <summary>Whether any timer is waiting for the clock to reach it.</summary>
    public bool HasTimers
    {
        get
        {
            lock (_timers)
                return _timers.Count > 0;
        }
    }

    public void Advance(TimeSpan by)
    {
        var now = Interlocked.Add(ref _ticks, by.Ticks);
        ManualTimer[] due;
        lock (_timers)
            due = _timers.Where(timer => timer.DueAt <= now).OrderBy(timer => timer.DueAt).ToArray();

        foreach (var timer in due)
            timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public long DueAt { get; private set; } = long.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timers)
            {
                clock._timers.Remove(this);
                _period = period;
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    DueAt = clock.GetTimestamp() + dueTime.Ticks;
                    clock._timers.Add(this);
                }
            }

            return true;
        }

        public void Fire()
        {
            lock (clock._timers)
            {
                if (!clock._timers.Remove(this))
                    return;
                if (_period != Timeout.InfiniteTimeSpan && _period > TimeSpan.Zero)
                {
                    DueAt += _period.Ticks;
                    clock._timers.Add(this);
                }
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (clock._timers)
                clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
