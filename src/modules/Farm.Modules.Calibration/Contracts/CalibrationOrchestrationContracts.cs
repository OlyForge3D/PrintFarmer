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
public sealed class CalibrationOrchestrationAdvanceRequest
{
    public string ClientId { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public bool? PrintCompleted { get; init; }

    public bool? PrintFailed { get; init; }
}
