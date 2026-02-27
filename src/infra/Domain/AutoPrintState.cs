namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents the current state of the auto-print ready-gate workflow for a printer.
/// </summary>
public enum AutoPrintState
{
    /// <summary>
    /// Auto-print is not active or has no pending action.
    /// </summary>
    None = 0,

    /// <summary>
    /// A print job has completed and the printer is waiting for operator confirmation
    /// that the bed is clear before dispatching the next queued job.
    /// </summary>
    PendingReady = 1,

    /// <summary>
    /// The operator has confirmed the bed is clear. The next queued job
    /// will be dispatched after passing filament pre-flight checks.
    /// </summary>
    Ready = 2,
}
