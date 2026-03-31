namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Service for synchronizing print job completion status when printers finish printing.
/// Called by subscription services (e.g., MoonrakerSubscriptionService) when printer state
/// transitions from "printing" to a completed state (standby, complete, idle).
/// </summary>
public interface IPrintJobCompletionService
{
    /// <summary>
    /// Marks the currently printing job for a printer as completed.
    /// This should be called when the printer state transitions from "printing" to "standby", "complete", or "idle".
    /// </summary>
    /// <param name="printerId">The ID of the printer that finished printing.</param>
    /// <param name="completionState">The final state (e.g., "standby", "complete", "idle").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if a job was marked as completed; false if no printing job was found.</returns>
    Task<bool> MarkCurrentJobAsCompletedAsync(Guid printerId, string completionState, CancellationToken ct = default);

    /// <summary>
    /// Marks the currently printing job for a printer as failed.
    /// This should be called when the printer state transitions to an error state or cancellation.
    /// </summary>
    /// <param name="printerId">The ID of the printer that encountered an error.</param>
    /// <param name="failureReason">The reason for the failure.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if a job was marked as failed; false if no printing job was found.</returns>
    Task<bool> MarkCurrentJobAsFailedAsync(Guid printerId, string failureReason, CancellationToken ct = default);

    /// <summary>
    /// Synchronizes orphaned jobs that are stuck in "Printing" status but the printer is now idle.
    /// This can happen if the API was restarted/redeployed while a print was in progress.
    /// Should be called on startup or manually via admin endpoint.
    /// </summary>
    /// <param name="printerStateLookup">
    /// A function that returns the current printer state for a given printer ID.
    /// Returns null if the printer state is unknown or offline.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of jobs that were synchronized.</returns>
    Task<int> SyncOrphanedPrintingJobsAsync(Func<Guid, string?> printerStateLookup, CancellationToken ct = default);

    /// <summary>
    /// Ensures a print job record exists for a printer that has transitioned to "Printing" state.
    /// If no active job (Starting/Printing) exists for the printer, creates a synthetic external
    /// print job to track the externally-started print (e.g., via OrcaSlicer "Upload and Print").
    /// External jobs are passive tracking records — they do not trigger auto-dispatch.
    /// </summary>
    /// <param name="printerId">The ID of the printer that started printing.</param>
    /// <param name="fileName">The filename from the printer status, if available.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if a new external print job was created; false if an active job already exists.</returns>
    Task<bool> EnsureExternalPrintJobExistsAsync(Guid printerId, string? fileName, CancellationToken ct = default);
}
