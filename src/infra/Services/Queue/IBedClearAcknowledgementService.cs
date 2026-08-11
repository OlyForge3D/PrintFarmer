using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Result of a bed-clear acknowledgement request.
/// </summary>
public enum BedClearAckOutcome
{
    /// <summary>New acknowledgement accepted asynchronously (202).</summary>
    Accepted,

    /// <summary>Exact replay of an already-accepted acknowledgement (200).</summary>
    Replayed,

    /// <summary>
    /// Job has already transitioned to Starting or Printing; the caller can
    /// treat this as success (200).
    /// </summary>
    AlreadyStartingOrPrinting,

    /// <summary>Job not found or caller lacks access (404).</summary>
    JobNotFound,

    /// <summary>Wrong job — the acknowledgement names a different job (409 wrong_job).</summary>
    WrongJob,

    /// <summary>Printer is busy with another job (409 printer_busy).</summary>
    PrinterBusy,

    /// <summary>Job is not in a dispatchable state (409 job_not_dispatchable).</summary>
    JobNotDispatchable,

    /// <summary>Idempotency key present but payload hash mismatch (409 idempotency_payload_mismatch).</summary>
    IdempotencyMismatch,

    /// <summary>If-Match precondition mismatch on dispatch state (412).</summary>
    DispatchRevisionConflict,

    /// <summary>If-Match header missing (428).</summary>
    PreconditionRequired,

    /// <summary>Calibration compatibility tuple/hashes/revision invalid (422 calibration_job_incompatible).</summary>
    CalibrationJobIncompatible,

    /// <summary>Hard filament gate failed (422 filament_check_failed).</summary>
    FilamentCheckFailed,

    /// <summary>Printer is offline or telemetry is stale (503).</summary>
    PrinterOfflineOrStale,

    /// <summary>Authorization failure — caller lacks required permissions (403).</summary>
    Forbidden,
}

/// <summary>
/// Request to acknowledge that the bed is clear and the job may start.
/// </summary>
/// <param name="JobId">Exact job the acknowledgement is scoped to.</param>
/// <param name="PrinterId">Printer the job is assigned to.</param>
/// <param name="ActorSubject">Authenticated subject of the operator.</param>
/// <param name="IdempotencyKey">Stable caller-supplied key for exact-replay detection.</param>
/// <param name="IfMatchJob">ETag of the exact urgent-head job.</param>
/// <param name="IfMatchDispatchState">
/// ETag of the printer dispatch state row (from a prior GET); required for
/// optimistic concurrency (If-Match).
/// </param>
/// <param name="ExpectedPrinterConfigRevision">
/// Printer configuration revision current at the time of the request.
/// Dispatch rejects the request if the printer has advanced beyond this value.
/// </param>
public sealed record AcknowledgeBedClearRequest(
    Guid JobId,
    Guid PrinterId,
    string ActorSubject,
    string IdempotencyKey,
    byte[]? IfMatchDispatchState,
    long? ExpectedPrinterConfigRevision,
    byte[]? IfMatchJob = null);

/// <summary>
/// Result of a bed-clear acknowledgement request.
/// </summary>
/// <param name="Outcome">Typed outcome.</param>
/// <param name="JobETag">Current ETag (row version) of the print job, when available.</param>
/// <param name="DispatchStateETag">Current ETag of the dispatch state, when available.</param>
/// <param name="ErrorDetail">Human-readable detail (no credentials or paths).</param>
/// <param name="BedClearCommandId">Exact durable command identity for accepted or replayed requests.</param>
/// <param name="BedClearIdempotencyKeySha256">Lower-case SHA-256 correlation of the exact case-sensitive UTF-8 key.</param>
public sealed record AcknowledgeBedClearResult(
    BedClearAckOutcome Outcome,
    byte[]? JobETag,
    byte[]? DispatchStateETag,
    string? ErrorDetail,
    Guid? BedClearCommandId = null,
    string? BedClearIdempotencyKeySha256 = null);

/// <summary>
/// Manages exact-job, one-use, expiring bed-clear acknowledgements used by
/// calibration jobs before dispatch. Each acknowledgement is scoped to a specific
/// job and printer; reorder, insertion of a higher-priority job, cancellation,
/// changed compatibility tuple, or expiry all invalidate it.
/// </summary>
public interface IBedClearAcknowledgementService
{
    /// <summary>
    /// Validates the request, persists the acknowledgement (or detects a replay),
    /// and optionally triggers an immediate dispatch attempt.
    /// </summary>
    /// <param name="request">Acknowledgement request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Typed outcome with ETag values for the client.</returns>
    Task<AcknowledgeBedClearResult> AcknowledgeAsync(AcknowledgeBedClearRequest request, CancellationToken ct = default);

    /// <summary>
    /// Invalidates any outstanding acknowledgement for the given printer if it
    /// no longer matches the front-of-queue job (called after reorder, insert,
    /// cancel, or compatibility change).
    /// </summary>
    /// <param name="printerId">Printer whose acknowledgement should be revalidated.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InvalidateStaleAcknowledgementsAsync(Guid printerId, CancellationToken ct = default);
}
