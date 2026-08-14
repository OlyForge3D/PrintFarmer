namespace Farm.Moonraker.Emulator.Domain;

/// <summary>
/// A deterministic, per-printer virtual clock. Time only moves when explicitly
/// advanced (via the control API) or, optionally, by a slow background ticker when
/// <see cref="Options.EmulatorOptions.TimeScale"/> is greater than zero. This keeps
/// contract tests reproducible: with the default TimeScale of 0, <see cref="UtcNow"/>
/// never changes on its own.
/// </summary>
public sealed class VirtualClock
{
    private static readonly DateTimeOffset Epoch = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly object _gate = new();
    private double _manualOffsetSeconds;
    private double _autoOffsetSeconds;

    /// <summary>The current deterministic virtual time.</summary>
    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return Epoch.AddSeconds(_manualOffsetSeconds + _autoOffsetSeconds);
            }
        }
    }

    /// <summary>Advances the virtual clock by an explicit amount (used by tests and the control API).</summary>
    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _manualOffsetSeconds += delta.TotalSeconds;
        }
    }

    /// <summary>Advances the virtual clock via the background auto-tick (real-elapsed-seconds * TimeScale).</summary>
    public void AutoAdvance(double seconds)
    {
        lock (_gate)
        {
            _autoOffsetSeconds += seconds;
        }
    }

    /// <summary>Resets the virtual clock back to the fixed epoch.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _manualOffsetSeconds = 0;
            _autoOffsetSeconds = 0;
        }
    }
}
