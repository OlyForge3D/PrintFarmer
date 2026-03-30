namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents the current state of the auto-dispatch ready-gate workflow for a printer.
/// </summary>
public enum AutoDispatchState
{
    /// <summary>
    /// Auto-dispatch is not active or has no pending action.
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

    /// <summary>
    /// The operator intentionally dismissed the current ready-gate prompt.
    /// Internally this suppresses stale PendingReady normalization until the
    /// next queueing or completion transition explicitly re-arms the gate.
    /// </summary>
    Dismissed = 3,
}
