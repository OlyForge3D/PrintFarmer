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
/// </remarks>
/// <remarks>
/// <b>Take-over semantics.</b> Advancing is full read-write adoption, not per-device ownership:
/// any caller with visibility into the orchestration's project (the owning user, or a farm admin)
/// may call this endpoint and drive an in-flight orchestration forward, whether or not it was the
/// device that started it - there is no per-device lock or reservation on an orchestration. That
/// is deliberate: the durable checkpoint (<see cref="CalibrationOrchestrationDto.CurrentStep"/>
/// and friends), not which device is calling, is what makes the saga correct, so a second device
/// picking up an unfinished orchestration is a supported scenario rather than an edge case to
/// block. Two devices racing to advance the <em>same</em> orchestration at the same time are kept
/// safe by <see cref="CalibrationOrchestrationDto.Revision"/>, an optimistic concurrency token:
/// the loser of the race receives a <c>calibration_orchestration_advance_conflict</c> 409 and
/// should refetch current state (e.g. via the project's in-flight query) rather than blindly
/// retrying its own now-stale view.
/// </remarks>
public sealed class CalibrationOrchestrationAdvanceRequest
{
    public string ClientId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public bool? PrintCompleted { get; init; }

    public bool? PrintFailed { get; init; }
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
/// <see cref="Orchestration"/> is the most recent non-terminal saga checkpoint for the project, if
/// any. Its <see cref="CalibrationOrchestrationDto.Status"/> and
/// <see cref="CalibrationOrchestrationDto.PrintJobId"/> distinguish a physical print already
/// underway (<c>Running</c> with a non-null <c>PrintJobId</c> - starting another wastes filament)
/// from a step merely in progress. <see cref="Drafts"/> lists every device with an uncommitted
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
