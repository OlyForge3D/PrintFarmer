using Farm.Web.Api.Contracts;

namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Durable, resumable orchestration of one calibration attempt from immutable specification to
/// promoted G-code.
/// </summary>
/// <remarks>
/// The saga spans two database contexts and one worker process, so it never claims a distributed
/// transaction. Every step commits its checkpoint before the side effect it describes, and a step whose
/// outcome is unknown is reconciled from durable evidence before it is retried. The same interface
/// serves the authenticated request path and the background recovery loop, which is what makes a crash
/// at any point recoverable without duplicating a slice job, an artifact or a promotion.
/// </remarks>
public interface ICalibrationGenerationSaga
{
    /// <summary>
    /// Starts, resumes or replays the generation run of one attempt.
    /// </summary>
    /// <param name="projectId">Owning calibration project.</param>
    /// <param name="attemptId">Immutable calibration attempt.</param>
    /// <param name="operationId">The caller's <c>Idempotency-Key</c> operation identifier.</param>
    /// <param name="request">The typed generation request.</param>
    /// <param name="actor">The authenticated caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>202</c> for a newly accepted or resumed run, <c>200</c> for an exact in-progress or completed
    /// replay, <c>409</c> for a changed payload or a changed immutable context, <c>412</c> for a stale
    /// revision, <c>422</c> for an unsupported or unsafe specification and <c>503</c> when a required
    /// production hop is unavailable.
    /// </returns>
    Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> CreateOrResumeAsync(
        Guid projectId,
        Guid attemptId,
        string? operationId,
        CalibrationGenerateJobRequest request,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    /// <summary>Returns the durable, redacted status of one orchestration.</summary>
    /// <param name="orchestrationId">The durable orchestration identity.</param>
    /// <param name="actor">The authenticated caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The redacted status, or a not-found failure.</returns>
    Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> GetStatusAsync(
        Guid orchestrationId,
        CalibrationActor actor,
        CancellationToken cancellationToken);

    /// <summary>Advances one orchestration as far as it can go without waiting.</summary>
    /// <param name="orchestrationId">The durable orchestration identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The durable status after the pass.</returns>
    Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> ResumeAsync(
        Guid orchestrationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves an orchestration whose last step outcome is unknown, without retrying the side effect
    /// until the real outcome is established.
    /// </summary>
    /// <param name="orchestrationId">The durable orchestration identity.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The durable status after reconciliation.</returns>
    Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> ReconcileAsync(
        Guid orchestrationId,
        CancellationToken cancellationToken);

    /// <summary>Resumes every orchestration whose next attempt is due.</summary>
    /// <param name="maxOrchestrations">Maximum number of orchestrations to touch in this pass.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of orchestrations that made durable progress.</returns>
    Task<int> RecoverDueAsync(int maxOrchestrations, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a run, but only while the existing aggregate still permits it.
    /// </summary>
    /// <param name="orchestrationId">The durable orchestration identity.</param>
    /// <param name="actor">The authenticated caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The cancelled status, or <c>409</c> when the run already owns work in another context and this
    /// aggregate has no semantics for withdrawing it.
    /// </returns>
    Task<CalibrationApiResult<CalibrationOrchestrationStatusDto>> CancelAsync(
        Guid orchestrationId,
        CalibrationActor actor,
        CancellationToken cancellationToken);
}
