using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Result of an atomic dispatch claim transaction.
/// </summary>
/// <param name="Success">True when the claim was acquired and the attempt row was written.</param>
/// <param name="Attempt">The persisted attempt record (populated on success).</param>
/// <param name="ErrorCode">Typed error code when <see cref="Success"/> is false.</param>
/// <param name="ErrorDetail">Human-readable description of the failure (no credentials or paths).</param>
public sealed record DispatchClaimResult(
    bool Success,
    QueueDispatchAttempt? Attempt,
    string? ErrorCode,
    string? ErrorDetail)
{
    /// <summary>Creates a successful claim result.</summary>
    public static DispatchClaimResult Ok(QueueDispatchAttempt attempt) =>
        new(true, attempt, null, null);

    /// <summary>Creates a failed claim result.</summary>
    public static DispatchClaimResult Fail(string errorCode, string errorDetail) =>
        new(false, null, errorCode, errorDetail);
}

/// <summary>
/// Parameters for acquiring a dispatch claim.
/// </summary>
/// <param name="JobId">Print job to claim.</param>
/// <param name="PrinterId">Printer to claim the job against.</param>
/// <param name="ActorSubject">Subject of the operator or system initiating dispatch.</param>
/// <param name="StartPathKind">Classification of the start path (e.g., Manual, Auto, BedClear).</param>
/// <param name="AcknowledgementIdempotencyKey">
/// Idempotency key of the bed-clear acknowledgement to consume, or null for
/// start paths that do not require an acknowledgement.
/// </param>
/// <param name="ExpectedJobRowVersion">
/// ETag / RowVersion for optimistic concurrency on the print job.
/// Null bypasses the check (trusted system callers only).
/// </param>
/// <param name="ExpectedDispatchStateRowVersion">
/// ETag / RowVersion for optimistic concurrency on the printer dispatch state.
/// </param>
public sealed record DispatchClaimRequest(
    Guid JobId,
    Guid PrinterId,
    string ActorSubject,
    string StartPathKind,
    string? AcknowledgementIdempotencyKey,
    byte[]? ExpectedJobRowVersion,
    byte[]? ExpectedDispatchStateRowVersion);

/// <summary>
/// Provides the single atomic cross-process dispatch claim used by every start path.
/// No start path sets <c>Status = Starting</c> or calls an upload/start adapter
/// without first acquiring a claim through this service.
/// </summary>
public interface IDispatchClaimService
{
    /// <summary>
    /// Atomically verifies pre-conditions and, on success, writes
    /// <c>PrintJob.Status = Starting</c>, the dispatch attempt row,
    /// the printer active-job reference, and the outbox event — all
    /// inside one database transaction that closes before any network I/O.
    /// </summary>
    /// <param name="request">Claim request parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Claim result indicating success or a typed failure reason.</returns>
    Task<DispatchClaimResult> AcquireClaimAsync(DispatchClaimRequest request, CancellationToken ct = default);

    /// <summary>
    /// Releases an active claim after a known pre-start failure, clearing the
    /// incorrect start time, preserving/re-arming safety state, and returning
    /// the job to a dispatchable state.
    /// </summary>
    /// <param name="attemptId">ID of the attempt to release.</param>
    /// <param name="errorCode">Typed error code to record on the attempt.</param>
    /// <param name="errorDetail">Human-readable error detail.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReleaseClaimOnKnownFailureAsync(Guid attemptId, string errorCode, string errorDetail, CancellationToken ct = default);

    /// <summary>
    /// Records a confirmed backend acceptance, advancing the job to Printing.
    /// </summary>
    /// <param name="attemptId">ID of the attempt to mark accepted.</param>
    /// <param name="backendJobId">External job identifier assigned by the printer backend.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordBackendAcceptedAsync(Guid attemptId, string? backendJobId, CancellationToken ct = default);

    /// <summary>
    /// Records an unknown outcome (network/protocol error) on the attempt.
    /// The job remains in Starting; a reconciliation pass must determine actual state.
    /// </summary>
    /// <param name="attemptId">ID of the attempt to mark as unknown.</param>
    /// <param name="errorDetail">Error detail to record.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordUnknownOutcomeAsync(Guid attemptId, string errorDetail, CancellationToken ct = default);
}
