using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.Administration.Controllers.Admin;

/// <summary>
/// One immutable, fully specified execution request. The request body is bound once and never
/// partially updated afterward; resubmitting the same <see cref="ReleaseId"/> resumes the same
/// journal-tracked operation (idempotent-by-release, see <c>HostUpdateExecutor</c>).
/// </summary>
public sealed record HostUpdateExecuteRequestBody(
    string ReleaseId,
    long AuthenticatedSequence,
    string ManifestDigest,
    string SourceCommit,
    string Channel,
    IReadOnlyList<HostUpdateExecutionTarget> Targets)
{
    public bool TryToExecutionRequest(out HostUpdateExecutionRequest? request, out string error)
    {
        request = null;
        if (!Enum.TryParse(Channel, ignoreCase: true, out HostUpdateExecutionChannel channel))
        {
            error = "channel_invalid";
            return false;
        }

        var candidate = new HostUpdateExecutionRequest(ReleaseId, AuthenticatedSequence, ManifestDigest, SourceCommit, channel, Targets);
        if (!candidate.IsValid(out error))
        {
            return false;
        }

        request = candidate;
        return true;
    }
}

/// <summary>Operator-visible status for one release's execution/recovery history.</summary>
public sealed record HostUpdateStatusResponse(
    string ReleaseId,
    HostUpdateExecutionState CurrentState,
    IReadOnlyList<HostUpdateExecutionActivity> Activities,
    HostUpdateRecoveryOutcomeRecord? RecoveryOutcome = null);

/// <summary>
/// Manual-first admin API for the host update executor (#2663). This is the operator-initiated
/// path only: it grants no standing automatic-scheduler permission, and every call is
/// authorized as a fresh administrative action. Automatic scheduling on this same engine is
/// #2666's later, separately authorized milestone.
/// </summary>
[ApiController]
[Route("api/admin/host-updates")]
[RequirePermission("system_settings", "admin")]
[Tags("Admin - Host Updates")]
public sealed class HostUpdateController(
    IHostUpdateExecutor executor,
    IHostUpdateExecutionJournal journal,
    IHostUpdateRecoveryCoordinator recoveryCoordinator,
    HostUpdateExecutionAvailabilityHolder availabilityHolder,
    IHostUpdateRecoveryOutcomeStore recoveryOutcomeStore) : ControllerBase
{
    /// <summary>
    /// Executes (or resumes) one manually approved host update to completion or
    /// <see cref="HostUpdateExecutionState.RecoveryRequired"/>. Never enables unattended
    /// automatic scheduling; this call must be made explicitly by an authorized operator.
    /// </summary>
    [HttpPost("execute")]
    [ProducesResponseType(typeof(HostUpdateStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HostUpdateStatusResponse>> ExecuteAsync(
        [FromBody] HostUpdateExecuteRequestBody body,
        CancellationToken cancellationToken)
    {
        if (TryRejectWhenUnavailable(out ActionResult? unavailable))
        {
            return unavailable!;
        }

        string error = "request_invalid";
        if (body is null || !body.TryToExecutionRequest(out HostUpdateExecutionRequest? request, out error))
        {
            return BadRequest(new { code = string.IsNullOrEmpty(error) ? "request_invalid" : error });
        }

        HostUpdateExecutionResult result = await executor.ExecuteAsync(request!, cancellationToken).ConfigureAwait(false);
        HostUpdateRecoveryOutcomeRecord? recoveryOutcome = await recoveryOutcomeStore.ReadAsync(result.ReleaseId, cancellationToken).ConfigureAwait(false);
        var response = new HostUpdateStatusResponse(result.ReleaseId, result.State, result.Activities, recoveryOutcome);
        return result.State == HostUpdateExecutionState.RecoveryRequired ? Conflict(response) : Ok(response);
    }

    /// <summary>Returns the durable, hash-chained journal history for one release.</summary>
    [HttpGet("{releaseId}/status")]
    [ProducesResponseType(typeof(HostUpdateStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HostUpdateStatusResponse>> GetStatusAsync(string releaseId, CancellationToken cancellationToken)
    {
        IReadOnlyList<HostUpdateExecutionActivity> activities = journal.Read(releaseId);
        if (activities.Count == 0)
        {
            return NotFound();
        }

        HostUpdateExecutionState current = activities[^1].State;
        HostUpdateRecoveryOutcomeRecord? recoveryOutcome = await recoveryOutcomeStore.ReadAsync(releaseId, cancellationToken).ConfigureAwait(false);
        return Ok(new HostUpdateStatusResponse(releaseId, current, activities, recoveryOutcome));
    }

    /// <summary>
    /// Attempts recovery for a release currently left in <see cref="HostUpdateExecutionState.RecoveryRequired"/>.
    /// Only ever performed explicitly by an authorized operator; never triggered automatically.
    /// </summary>
    [HttpPost("{releaseId}/recover")]
    [ProducesResponseType(typeof(HostUpdateRecoveryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HostUpdateRecoveryResult>> RecoverAsync(
        string releaseId,
        [FromBody] HostUpdateExecuteRequestBody body,
        CancellationToken cancellationToken)
    {
        if (TryRejectWhenUnavailable(out ActionResult? unavailable))
        {
            return unavailable!;
        }

        IReadOnlyList<HostUpdateExecutionActivity> activities = journal.Read(releaseId);
        if (activities.Count == 0)
        {
            return NotFound();
        }

        if (activities[^1].State != HostUpdateExecutionState.RecoveryRequired)
        {
            return Conflict(new { code = "not_in_recovery" });
        }

        string error = "request_invalid";
        if (body is null || !body.TryToExecutionRequest(out HostUpdateExecutionRequest? request, out error))
        {
            return BadRequest(new { code = string.IsNullOrEmpty(error) ? "request_invalid" : error });
        }

        HostUpdateRecoveryResult result = await recoveryCoordinator.RecoverAsync(request!, activities, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Bishop/Hicks review (issue #2663): the executor's positively-proven availability (root
    /// writable, journal intact, every required adapter/writer wired, restart reconciliation
    /// clean -- see <see cref="HostUpdateExecutionAvailabilityProvider"/>) must gate every
    /// mutating admin call, not just be exposed as an informational status a caller could choose
    /// to ignore. A request that arrives while the executor is <c>Unavailable</c> (including,
    /// critically, while restart reconciliation still reports an unresolved prior release) is
    /// rejected before it ever touches <see cref="IHostUpdateExecutor"/> or
    /// <see cref="IHostUpdateRecoveryCoordinator"/>, with the exact unavailability reasons
    /// surfaced to the operator rather than an opaque failure deeper in the pipeline.
    /// </summary>
    private bool TryRejectWhenUnavailable(out ActionResult? result)
    {
        HostUpdateExecutionAvailability availability = availabilityHolder.Current;
        if (availability.State == HostUpdateExecutionAvailabilityState.Available)
        {
            result = null;
            return false;
        }

        result = StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = "host_update_executor_unavailable", reasons = availability.Reasons });
        return true;
    }
}
