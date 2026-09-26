// <copyright file="ManualTimeProvider.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

namespace Farm.Testing.Shared;

/// <summary>
/// Thread-safe fake <see cref="TimeProvider"/> whose wall clock, monotonic timestamp and
/// timers only move when a test calls <see cref="Advance"/>. Timers created through
/// <see cref="CreateTimer"/> (including <c>Task.Delay(TimeSpan, TimeProvider, ...)</c>)
/// fire synchronously inside <see cref="Advance"/> once their due time is reached (a zero due
/// time fires on the next <see cref="Advance"/>, including <c>Advance(TimeSpan.Zero)</c>), so
/// hosted polling loops can be stepped deterministically without sleeping.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow;
    private long _timestamp;

    /// <summary>Initializes a new instance of the <see cref="ManualTimeProvider"/> class.</summary>
    /// <param name="utcNow">Initial UTC instant.</param>
    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow.ToUniversalTime();
    }

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Gets the number of timers that are currently scheduled to fire.</summary>
    public int ActiveTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(timer => timer.IsScheduled);
            }
        }
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        _ = timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer that has become due.</summary>
    /// <param name="elapsed">Non-negative amount of time to advance.</param>
    public void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        List<ManualTimer> due;
        long timestamp;
        lock (_gate)
        {
            _utcNow += elapsed;
            _timestamp = checked(_timestamp + elapsed.Ticks);
            timestamp = _timestamp;
            due = _timers.Where(timer => timer.TryClaimDue(timestamp)).ToList();
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    /// <summary>Waits (in real time) until at least <paramref name="count"/> timers are scheduled.</summary>
    /// <param name="count">Minimum number of scheduled timers.</param>
    /// <param name="timeout">Real-time upper bound for the wait.</param>
    /// <returns>A task that completes once the timers are scheduled.</returns>
    public async Task WaitForActiveTimersAsync(int count, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (ActiveTimerCount < count)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), cts.Token).ConfigureAwait(false);
        }
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private long? _dueTimestamp;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool IsScheduled => _dueTimestamp.HasValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : checked(owner._timestamp + dueTime.Ticks);
                _period = period;
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _dueTimestamp = null;
                _ = owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        // Caller holds owner._gate.
        public bool TryClaimDue(long timestamp)
        {
            if (_dueTimestamp is not long dueTimestamp || timestamp < dueTimestamp)
            {
                return false;
            }

            _dueTimestamp = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero
                ? null
                : checked(timestamp + _period.Ticks);
            return true;
        }

        public void Fire() => callback(state);
    }
}
