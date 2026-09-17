using Xunit;

namespace Farm.Modules.Printers.Tests.Controllers;

internal sealed class DirectControlTestClock : TimeProvider
{
    private readonly object sync = new();
    private readonly List<ControlTimer> timers = [];
    private TimeSpan elapsed;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (sync)
        {
            var timer = new ControlTimer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            return timer;
        }
    }

    public void Advance(TimeSpan duration)
    {
        lock (sync)
        {
            elapsed += duration;
            foreach (ControlTimer timer in timers.ToArray())
            {
                timer.FireIfDue();
            }
        }
    }

    private sealed class ControlTimer(DirectControlTestClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan? due;
        private bool disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            lock (clock.sync)
            {
                if (disposed)
                {
                    return false;
                }

                due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.elapsed + dueTime;
                return true;
            }
        }

        public void FireIfDue()
        {
            if (!disposed && due is TimeSpan at && at <= clock.elapsed)
            {
                due = null;
                callback(state);
            }
        }

        public void Dispose()
        {
            lock (clock.sync)
            {
                disposed = true;
                due = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
