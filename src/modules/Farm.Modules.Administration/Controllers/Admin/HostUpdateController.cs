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
    IReadOnlyList<HostUpdateExecutionActivity> Activities);

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
    IHostUpdateRecoveryCoordinator recoveryCoordinator) : ControllerBase
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
    public async Task<ActionResult<HostUpdateStatusResponse>> ExecuteAsync(
        [FromBody] HostUpdateExecuteRequestBody body,
        CancellationToken cancellationToken)
    {
        string error = "request_invalid";
        if (body is null || !body.TryToExecutionRequest(out HostUpdateExecutionRequest? request, out error))
        {
            return BadRequest(new { code = string.IsNullOrEmpty(error) ? "request_invalid" : error });
        }

        HostUpdateExecutionResult result = await executor.ExecuteAsync(request!, cancellationToken).ConfigureAwait(false);
        var response = new HostUpdateStatusResponse(result.ReleaseId, result.State, result.Activities);
        return result.State == HostUpdateExecutionState.RecoveryRequired ? Conflict(response) : Ok(response);
    }

    /// <summary>Returns the durable, hash-chained journal history for one release.</summary>
    [HttpGet("{releaseId}/status")]
    [ProducesResponseType(typeof(HostUpdateStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<HostUpdateStatusResponse> GetStatus(string releaseId)
    {
        IReadOnlyList<HostUpdateExecutionActivity> activities = journal.Read(releaseId);
        if (activities.Count == 0)
        {
            return NotFound();
        }

        HostUpdateExecutionState current = activities[^1].State;
        return Ok(new HostUpdateStatusResponse(releaseId, current, activities));
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
    public async Task<ActionResult<HostUpdateRecoveryResult>> RecoverAsync(
        string releaseId,
        [FromBody] HostUpdateExecuteRequestBody body,
        CancellationToken cancellationToken)
    {
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
}
