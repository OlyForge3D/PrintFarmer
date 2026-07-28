using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Result of an atomic dispatch claim transaction.
/// </summary>
/// <param name="Success">True when the claim was acquired and the attempt row was written.</param>
/// <param name="Attempt">The persisted attempt record (populated on success).</param>
/// <param name="ErrorCode">Typed error code when <see cref="Success"/> is false.</param>
/// <param name="ErrorDetail">Human-readable description of the failure (no credentials or paths).</param>
/// <param name="IsPreconditionFailure">
/// True when the failure was caused by a caller-supplied revision precondition
/// (<c>If-Match</c>) that no longer matches — maps to HTTP 412 rather than 409.
/// </param>
public sealed record DispatchClaimResult(
    bool Success,
    QueueDispatchAttempt? Attempt,
    string? ErrorCode,
    string? ErrorDetail,
    bool IsPreconditionFailure = false)
{
    /// <summary>Creates a successful claim result.</summary>
    /// <param name="attempt">The persisted attempt row.</param>
    /// <returns>A successful result.</returns>
    public static DispatchClaimResult Ok(QueueDispatchAttempt attempt) =>
        new(true, attempt, null, null);

    /// <summary>Creates a failed claim result.</summary>
    /// <param name="errorCode">Typed error code.</param>
    /// <param name="errorDetail">Human-readable detail.</param>
    /// <returns>A failed result.</returns>
    public static DispatchClaimResult Fail(string errorCode, string errorDetail) =>
        new(false, null, errorCode, errorDetail);

    /// <summary>Creates a failed claim result caused by a stale caller-supplied revision.</summary>
    /// <param name="errorCode">Typed error code.</param>
    /// <param name="errorDetail">Human-readable detail.</param>
    /// <returns>A failed result flagged as a precondition failure.</returns>
    public static DispatchClaimResult PreconditionFailed(string errorCode, string errorDetail) =>
        new(false, null, errorCode, errorDetail, IsPreconditionFailure: true);
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
/// Parameters for an ad-hoc (non-queue) printer start, such as the slice→print bridge or
/// the printer file-start endpoint. Ad-hoc starts must still pass the shared printer gates
/// and be recorded as durable dispatch attempts so a printer can never be started twice.
/// </summary>
/// <param name="PrinterId">Printer to claim.</param>
/// <param name="ActorSubject">Subject of the operator initiating the start.</param>
/// <param name="StartPathKind">Classification of the start path (e.g., SliceBridge, PrinterFile).</param>
/// <param name="BackendFileName">File name that will be presented to the backend.</param>
/// <param name="UseDeterministicFileName">
/// Whether the caller uploads bytes and can therefore present the attempt-scoped file name.
/// Existing printer-local files retain their exact persisted name.
/// </param>
public sealed record AdHocDispatchClaimRequest(
    Guid PrinterId,
    string ActorSubject,
    string StartPathKind,
    string BackendFileName,
    bool UseDeterministicFileName = true);

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
    /// the printer active-job reference, the job state-history row, the audit row,
    /// and the outbox event — all inside one database transaction that closes before
    /// any network I/O.
    /// </summary>
    /// <param name="request">Claim request parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Claim result indicating success or a typed failure reason.</returns>
    Task<DispatchClaimResult> AcquireClaimAsync(DispatchClaimRequest request, CancellationToken ct = default);

    /// <summary>
    /// Atomically claims a printer for an ad-hoc start that has no queue job.
    /// Applies the same printer gates (enabled, available, not in maintenance, no active
    /// lease, telemetry not printing) and writes a durable attempt plus audit row before
    /// the caller performs any network I/O.
    /// </summary>
    /// <param name="request">Ad-hoc claim request parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Claim result indicating success or a typed failure reason.</returns>
    Task<DispatchClaimResult> AcquireAdHocClaimAsync(AdHocDispatchClaimRequest request, CancellationToken ct = default);

    /// <summary>Persists that the caller is about to invoke the backend.</summary>
    Task<bool> RecordBackendCallStartedAsync(Guid attemptId, CancellationToken ct = default);

    /// <summary>
    /// Releases an active claim after a known pre-start failure, clearing the
    /// incorrect start time, preserving/re-arming safety state, and returning
    /// the job to a dispatchable state.
    /// </summary>
    /// <param name="attemptId">ID of the attempt to release.</param>
    /// <param name="errorCode">Typed error code to record on the attempt.</param>
    /// <param name="errorDetail">Human-readable error detail.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ReleaseClaimOnKnownFailureAsync(Guid attemptId, string errorCode, string errorDetail, CancellationToken ct = default);

    /// <summary>
    /// Records a confirmed backend acceptance, advancing the job to Printing.
    /// </summary>
    /// <param name="attemptId">ID of the attempt to mark accepted.</param>
    /// <param name="backendJobId">External job identifier assigned by the printer backend.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> RecordBackendAcceptedAsync(Guid attemptId, string? backendJobId, CancellationToken ct = default);

    /// <summary>Records acceptance with distinct provider job and file identities.</summary>
    Task<bool> RecordBackendAcceptedAsync(
        Guid attemptId,
        string? backendJobId,
        string? backendFileIdentity,
        CancellationToken ct = default);

    /// <summary>
    /// Records an unknown outcome (network/protocol error) on the attempt.
    /// The job remains in Starting; a reconciliation pass must determine actual state.
    /// </summary>
    /// <param name="attemptId">ID of the attempt to mark as unknown.</param>
    /// <param name="errorDetail">Error detail to record.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> RecordUnknownOutcomeAsync(Guid attemptId, string errorDetail, CancellationToken ct = default);
}
