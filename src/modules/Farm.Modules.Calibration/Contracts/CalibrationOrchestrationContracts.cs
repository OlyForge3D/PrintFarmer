namespace Farm.Modules.Calibration.Contracts;

/// <summary>
/// Durable saga checkpoint for one calibration attempt's execution run. This is a byproduct of
/// the attempt's append-only <c>CalibrationAttemptEvent</c> timeline, never a gate an operator
/// must satisfy before starting a calibration.
/// </summary>
public sealed record CalibrationOrchestrationDto(
    Guid Id,
    Guid ProjectId,
    Guid AttemptId,
    string CurrentStep,
    string Status,
    int RetryCount,
    DateTime? NextRetryAtUtc,
    string? LastErrorCode,
    Guid? SliceJobId,
    Guid? GcodeFileId,
    Guid? PrintJobId,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? CompletedAtUtc);

/// <summary>
/// Request to run the saga forward by one step from its current checkpoint.
/// </summary>
/// <remarks>
/// <see cref="PrintCompleted"/> and <see cref="PrintFailed"/> are only consulted while the
/// orchestration is parked on the <c>awaiting-print</c> step; every other step advances purely
/// from state the saga already owns (a submitted slice job's status, or the presence of an
/// operator-submitted <c>CalibrationObservation</c>) so calling this endpoint never becomes a new
/// precondition an operator must satisfy - it only reports and drives forward work that was going
/// to happen anyway.
///
/// <b>Take-over semantics.</b> Advancing is full read-write adoption, not per-device ownership:
/// any caller with visibility into the orchestration's project (the owning user, or a farm admin)
/// may call this endpoint and drive an in-flight orchestration forward, whether or not it was the
/// device that started it - there is no per-device lock or reservation on an orchestration. That
/// is deliberate: the durable checkpoint (<see cref="CalibrationOrchestrationDto.CurrentStep"/>
/// and friends), not which device is calling, is what makes the saga correct, so a second device
/// picking up an unfinished orchestration is a supported scenario rather than an edge case to
/// block.
///
/// Two devices racing to advance the <em>same</em> orchestration are kept safe by
/// <see cref="ExpectedRevision"/>, which a caller should populate from the last
/// <see cref="CalibrationOrchestrationDto.Revision"/> it observed (e.g. via the project's
/// in-flight query, or a prior advance response). When supplied and it no longer matches the
/// orchestration's current <c>Revision</c>, the call is rejected immediately with a
/// <c>calibration_orchestration_advance_conflict</c> 409 - before any step logic or side effects
/// run - and the caller should refetch current state rather than blindly retrying its now-stale
/// view. <see cref="ExpectedRevision"/> is optional for backward compatibility: omitting it falls
/// back to unconditional best-effort advancement against whatever the current checkpoint is (the
/// in-process serialization lock still prevents two overlapping calls from both acting on the
/// same stale snapshot and duplicating an external side effect, but does not by itself detect
/// that a caller's specific intent - e.g. "I'm reporting on awaiting-print" - has been superseded
/// by the time its turn comes). New take-over-aware clients should always supply
/// <see cref="ExpectedRevision"/>. A residual <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>-driven
/// 409 also exists as defense in depth for the multi-instance case, where two API processes each
/// hold their own independent in-process lock.
///
/// <b>Cross-process external-dispatch race - closed for the two steps with external side effects
/// (issue #2188).</b> The gap previously documented here - that two separate API process
/// instances could each pass the <see cref="ExpectedRevision"/> staleness check and both begin a
/// step's external side effect before either one's write reached the database - is now closed for
/// the two steps that ever call an external gateway: <c>slicing</c> (<c>ISliceSubmissionGateway.SubmitAsync</c>)
/// and <c>sending-to-printer</c> (<c>IPrintDispatchGateway.SendToPrinterAsync</c>). Both now
/// acquire an atomic, row-version-fenced claim/lease (<c>CalibrationOrchestration.LeaseOwner</c>/
/// <c>LeaseExpiresAtUtc</c>, mirroring the <c>PrinterDispatchState</c>/<c>QueueDispatchAttempt</c>
/// claim-before-dispatch shape used for print-queue dispatch) immediately before calling their
/// gateway, and a losing claim attempt returns the existing <c>calibration_orchestration_advance_conflict</c>
/// 409 without ever calling the gateway - see
/// <c>CalibrationOrchestrationSagaService.TryAcquireStepClaimAsync</c> for the full design,
/// including why the lease-expiry check is required in addition to <see cref="ExpectedRevision"/>/
/// <c>Revision</c> fencing, and the lease-duration rationale. The remaining eight saga steps
/// (<c>cloning-profile</c>, <c>awaiting-slice</c>, <c>awaiting-print</c>,
/// <c>awaiting-measurement</c>, <c>applying-measurement</c>, <c>advancing</c>, plus the terminal
/// <c>created</c>/<c>completed</c> steps) have no external side effect to fence and are
/// unaffected.
///
/// <b>Residual risk accepted for v1 (documented, not closed).</b> Neither
/// <c>SliceJobController</c>'s <c>POST /api/slice</c> nor <c>SlicePrintBridgeController</c>'s
/// send-to-printer endpoint accepts a caller-supplied idempotency/correlation key today (unlike
/// <c>QueueDispatchAttempt.BackendCommandId</c>/<c>BackendCorrelationId</c> for print-queue
/// dispatch), so a *reclaim* of a lease that has genuinely expired - but whose original holder is
/// merely slow, not crashed - can still result in two real external dispatches for the same step.
/// This is deliberately accepted for v1 rather than changing those two controllers' own request
/// contracts, which would be a cross-cutting change outside this saga; see
/// <c>TryAcquireStepClaimAsync</c>'s remarks for the full reasoning. A full transactional-outbox
/// migration of calibration dispatch remains an optional future stretch goal, not attempted here.
/// </remarks>
public sealed class CalibrationOrchestrationAdvanceRequest
{
    public string ClientId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public bool? PrintCompleted { get; init; }

    public bool? PrintFailed { get; init; }

    /// <summary>
    /// The <see cref="CalibrationOrchestrationDto.Revision"/> this caller last observed. When
    /// supplied and stale, the advance is rejected with a
    /// <c>calibration_orchestration_advance_conflict</c> 409 instead of proceeding against
    /// whatever the orchestration's current state now is. See the take-over semantics remarks
    /// above.
    /// </summary>
    public long? ExpectedRevision { get; init; }
}

/// <summary>
/// Existence-only signal that some device has an uncommitted draft for a step, without exposing
/// its content.
/// </summary>
/// <remarks>
/// <see cref="DeviceLabel"/> is the client-supplied device-lineage id itself. There is no separate
/// device-registry or friendly-name entity in this system, and the lineage id is not draft content
/// (it never carries calibration values, prerequisites, or method selections), so it safely
/// doubles as the human-readable label a second device shows an operator, e.g. "already started on
/// device-lineage-id".
/// </remarks>
public sealed record CalibrationDraftExistenceDto(
    string StepId,
    string DeviceLabel,
    DateTime UpdatedAtUtc);

/// <summary>
/// The single project-scoped answer to "is anything already underway here, and where was it
/// started?" for a fresh device that has no attempt or orchestration id to query by.
/// </summary>
/// <remarks>
/// <see cref="Orchestration"/> is the highest-priority non-terminal saga checkpoint for the
/// project, if any - see <c>CalibrationProjectService.InFlightPriority</c> for why "most recently
/// touched" alone is not a safe pick. Its <see cref="CalibrationOrchestrationDto.Status"/> and
/// <see cref="CalibrationOrchestrationDto.CurrentStep"/> distinguish a physical print already
/// underway from a step merely in progress: <c>Running</c> at <c>awaiting-print</c> means gcode
/// has actually been uploaded and a print started on a printer - starting another wastes filament
/// - not merely that a step is mid-flight. <see cref="CalibrationOrchestrationDto.PrintJobId"/> is
/// also checked when present, for forward compatibility, but the saga's ad-hoc print-dispatch path
/// does not create a queued print job today, so it is normally null even while a print is
/// genuinely running; <c>CurrentStep</c> is the signal that is actually populated. <see
/// cref="Drafts"/> lists every device with an uncommitted
/// step draft, by existence only: draft CONTENT never crosses devices, only the fact that one
/// exists, which step it is for, and when it was last touched. A project can have both an
/// in-flight orchestration and unrelated device drafts at once; a client should treat the
/// orchestration as authoritative for "is a print running" and the draft list as authoritative for
/// "did someone start filling in a step that was never submitted."
/// </remarks>
public sealed record CalibrationInFlightStateDto(
    Guid ProjectId,
    CalibrationOrchestrationDto? Orchestration,
    IReadOnlyList<CalibrationDraftExistenceDto> Drafts);
