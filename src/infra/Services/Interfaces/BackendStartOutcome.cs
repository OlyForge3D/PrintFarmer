namespace Farm.Infrastructure.Services.Interfaces;

/// <summary>
/// Typed outcome of a durable backend-start command execution (issue #900, defect 6).
///
/// The durable command consumer marks the outbox row
/// <c>Published</c> ONLY for <see cref="BackendStartStatus.Accepted"/>. Every other status
/// keeps the row leased or schedules a bounded retry so a command is never
/// declared delivered on an unknown or failed backend outcome.
/// </summary>
public enum BackendStartStatus
{
    /// <summary>The backend confirmed acceptance of the start command.</summary>
    Accepted = 0,

    /// <summary>
    /// The command was a no-op because the job had already advanced past the queue
    /// (Starting/Printing or terminal). Safe to complete.
    /// </summary>
    AlreadyStarted = 1,

    /// <summary>
    /// A guard rejected the start deterministically (claim denied, artifact unavailable,
    /// backend rejected). Retrying without operator action cannot succeed.
    /// </summary>
    RejectedPermanently = 2,

    /// <summary>
    /// A known transient failure occurred. Retrying later may succeed.
    /// </summary>
    RejectedTransiently = 3,

    /// <summary>
    /// The backend outcome could not be determined (network/protocol error after the
    /// command may have been delivered). The lease is retained and reconciliation must
    /// determine the real state; the command is NEVER retried blindly.
    /// </summary>
    Unknown = 4,
}

/// <summary>
/// Typed result of <c>IPrintJobManagementService.DispatchJobWithAckAsync</c>.
/// </summary>
/// <param name="Status">Typed status of the start command.</param>
/// <param name="AttemptId">Dispatch attempt created for this command, when a claim was acquired.</param>
/// <param name="ErrorCode">Typed error code on non-accepted statuses.</param>
/// <param name="ErrorDetail">Human-readable detail (no credentials or paths).</param>
public sealed record BackendStartOutcome(
    BackendStartStatus Status,
    Guid? AttemptId,
    string? ErrorCode,
    string? ErrorDetail)
{
    /// <summary>Creates an accepted outcome.</summary>
    /// <param name="attemptId">Attempt that was accepted.</param>
    /// <returns>An accepted outcome.</returns>
    public static BackendStartOutcome Accepted(Guid attemptId) =>
        new(BackendStartStatus.Accepted, attemptId, null, null);

    /// <summary>Creates an already-started (idempotent no-op) outcome.</summary>
    /// <param name="detail">Human-readable detail.</param>
    /// <returns>An already-started outcome.</returns>
    public static BackendStartOutcome AlreadyStarted(string detail) =>
        new(BackendStartStatus.AlreadyStarted, null, "already_started", detail);

    /// <summary>Creates a permanently-rejected outcome.</summary>
    /// <param name="errorCode">Typed error code.</param>
    /// <param name="detail">Human-readable detail.</param>
    /// <param name="attemptId">Attempt id when a claim had been acquired.</param>
    /// <returns>A permanently-rejected outcome.</returns>
    public static BackendStartOutcome RejectedPermanently(string errorCode, string detail, Guid? attemptId = null) =>
        new(BackendStartStatus.RejectedPermanently, attemptId, errorCode, detail);

    /// <summary>Creates a transiently-rejected outcome.</summary>
    /// <param name="errorCode">Typed error code.</param>
    /// <param name="detail">Human-readable detail.</param>
    /// <param name="attemptId">Attempt id when a claim had been acquired.</param>
    /// <returns>A transiently-rejected outcome.</returns>
    public static BackendStartOutcome RejectedTransiently(string errorCode, string detail, Guid? attemptId = null) =>
        new(BackendStartStatus.RejectedTransiently, attemptId, errorCode, detail);

    /// <summary>Creates an unknown outcome that requires reconciliation.</summary>
    /// <param name="detail">Human-readable detail.</param>
    /// <param name="attemptId">Attempt id that must be reconciled.</param>
    /// <returns>An unknown outcome.</returns>
    public static BackendStartOutcome Unknown(string detail, Guid? attemptId) =>
        new(BackendStartStatus.Unknown, attemptId, "backend_outcome_unknown", detail);
}
