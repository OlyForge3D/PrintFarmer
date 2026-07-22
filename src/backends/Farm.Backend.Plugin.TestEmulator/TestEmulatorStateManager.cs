using System.Collections.Concurrent;

namespace Farm.Backend.Plugin.TestEmulator;

/// <summary>
/// Tracks the simulated state for each emulated printer.
/// Singleton shared between the client (for mutations) and the polling service (for reads).
/// </summary>
public sealed class TestEmulatorStateManager
{
    private readonly ConcurrentDictionary<Guid, EmulatedPrinterState> _states = new();

    /// <summary>
    /// Registers a printer with the given initial state.
    /// </summary>
    public void Register(Guid printerId, EmulatedPrinterState state) =>
        _states[printerId] = state;

    /// <summary>
    /// Gets the current state for a printer, or null if not registered.
    /// </summary>
    public EmulatedPrinterState? GetState(Guid printerId) =>
        _states.TryGetValue(printerId, out var state) ? state : null;

    /// <summary>
    /// Gets all registered printer IDs.
    /// </summary>
    public IReadOnlyCollection<Guid> GetAllPrinterIds() => _states.Keys.ToList();

    /// <summary>
    /// Transitions a printer to the Printing state.
    /// </summary>
    public void StartPrint(Guid printerId, int durationSeconds = 60)
    {
        if (_states.TryGetValue(printerId, out var state))
        {
            state.State = EmulatorPrinterState.Printing;
            state.Progress = 0;
            state.PrintStartedAt = DateTime.UtcNow;
            state.PrintDurationSeconds = durationSeconds;
            state.JobName = "test-print-benchy.gcode";
        }
    }

    /// <summary>
    /// Pauses the current print.
    /// </summary>
    public void Pause(Guid printerId)
    {
        if (_states.TryGetValue(printerId, out var state) && state.State == EmulatorPrinterState.Printing)
        {
            state.PausedAt = DateTime.UtcNow;
            state.State = EmulatorPrinterState.Paused;
        }
    }

    /// <summary>
    /// Resumes a paused print.
    /// </summary>
    public void Resume(Guid printerId)
    {
        if (_states.TryGetValue(printerId, out var state) && state.State == EmulatorPrinterState.Paused)
        {
            state.State = EmulatorPrinterState.Printing;

            // Adjust start time to account for pause duration
            if (state.PausedAt.HasValue)
            {
                var pauseDuration = DateTime.UtcNow - state.PausedAt.Value;
                state.PrintStartedAt = state.PrintStartedAt?.Add(pauseDuration);
                state.PausedAt = null;
            }
        }
    }

    /// <summary>
    /// Cancels the current print and returns to Idle.
    /// </summary>
    public void Cancel(Guid printerId)
    {
        if (_states.TryGetValue(printerId, out var state))
        {
            state.State = EmulatorPrinterState.Idle;
            state.Progress = 0;
            state.JobName = null;
            state.PrintStartedAt = null;
            state.PausedAt = null;
        }
    }

    /// <summary>
    /// Marks a printer as having completed its print. Called by the polling service.
    /// </summary>
    public void MarkComplete(Guid printerId)
    {
        if (_states.TryGetValue(printerId, out var state))
        {
            state.State = EmulatorPrinterState.Complete;
            state.CompletedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Resets a completed printer back to Idle. Called by the polling service after dwell time.
    /// </summary>
    public void ResetToIdle(Guid printerId)
    {
        if (_states.TryGetValue(printerId, out var state))
        {
            state.State = EmulatorPrinterState.Idle;
            state.Progress = 0;
            state.JobName = null;
            state.PrintStartedAt = null;
            state.CompletedAt = null;
        }
    }
}

/// <summary>
/// Mutable state for a single emulated printer.
/// </summary>
public sealed class EmulatedPrinterState
{
    public EmulatorPrinterState State { get; set; } = EmulatorPrinterState.Idle;

    public double Progress { get; set; }

    public string? JobName { get; set; }

    public DateTime? PrintStartedAt { get; set; }

    public DateTime? PausedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int PrintDurationSeconds { get; set; } = 60;

    /// <summary>Ambient temperature in °C.</summary>
    public const double AmbientTemp = 20.0;

    /// <summary>Target hotend temperature when printing.</summary>
    public const double TargetHotendTemp = 215.0;

    /// <summary>Target bed temperature when printing.</summary>
    public const double TargetBedTemp = 60.0;

    /// <summary>Temperature ramp time in seconds.</summary>
    public const double TempRampSeconds = 10.0;

    /// <summary>
    /// Calculates current hotend temperature based on state and elapsed time.
    /// </summary>
    public double GetHotendTemp()
    {
        if (State is not (EmulatorPrinterState.Printing or EmulatorPrinterState.Paused))
        {
            return AmbientTemp;
        }

        return CalculateRampedTemp(TargetHotendTemp);
    }

    /// <summary>
    /// Calculates current bed temperature based on state and elapsed time.
    /// </summary>
    public double GetBedTemp()
    {
        if (State is not (EmulatorPrinterState.Printing or EmulatorPrinterState.Paused))
        {
            return AmbientTemp;
        }

        return CalculateRampedTemp(TargetBedTemp);
    }

    private double CalculateRampedTemp(double target)
    {
        if (PrintStartedAt is null)
        {
            return AmbientTemp;
        }

        double elapsed = (DateTime.UtcNow - PrintStartedAt.Value).TotalSeconds;
        double rampFraction = Math.Min(elapsed / TempRampSeconds, 1.0);
        return AmbientTemp + ((target - AmbientTemp) * rampFraction);
    }
}

/// <summary>
/// Canonical emulator printer states.
/// </summary>
public enum EmulatorPrinterState
{
    Idle,
    Printing,
    Paused,
    Complete,
    Error,
    Offline
}
