using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.Administration.Controllers.Admin;

/// <summary>Operator-visible status for one release's execution/recovery history.</summary>
public sealed record HostUpdateStatusResponse(
    string ReleaseId,
    HostUpdateExecutionState CurrentState,
    IReadOnlyList<HostUpdateExecutionActivity> Activities);

public sealed record HostUpdateRecoveryRequestBody(string? RequestId = null);

/// <summary>
/// Manual-first admin API for the host update executor (#2663/#2666). The API accepts only
/// operator intent/authorization references; immutable release identity, sequence, manifest,
/// channel, trust root, platform, and execution target digests are resolved server-side from
/// signed verified evidence and protected authorization state immediately before execution.
/// </summary>
[ApiController]
[Route("api/admin/host-updates")]
[RequirePermission("system_settings", "admin")]
[Tags("Admin - Host Updates")]
public sealed class HostUpdateController(
    IHostUpdateExecutor executor,
    IHostUpdateExecutionRequestResolver requestResolver,
    IHostUpdateExecutionJournal journal,
    IHostUpdateRecoveryCoordinator recoveryCoordinator) : ControllerBase
{
    [HttpPost("authorizations")]
    [ProducesResponseType(typeof(HostUpdateManualAuthorizationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HostUpdateManualAuthorizationResponse>> AuthorizeAsync(
        [FromBody] HostUpdateManualAuthorizationIntent? intent,
        CancellationToken cancellationToken)
    {
        try
        {
            HostUpdateManualAuthorizationResponse response = await requestResolver.AuthorizeCurrentAsync(
                intent ?? new HostUpdateManualAuthorizationIntent(),
                cancellationToken).ConfigureAwait(false);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { code = ex.Message });
        }
    }

    /// <summary>
    /// Executes one manually approved host update. The body may reference an existing one-time
    /// authorization ID or be empty to atomically authorize the current verified candidate once;
    /// it never carries target release material supplied by the client.
    /// </summary>
    [HttpPost("execute")]
    [ProducesResponseType(typeof(HostUpdateStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HostUpdateStatusResponse>> ExecuteAsync(
        [FromBody] HostUpdateManualAuthorizationIntent? intent,
        CancellationToken cancellationToken)
    {
        HostUpdateExecutionResolutionResult resolution = await requestResolver.ResolveManualAsync(
            intent ?? new HostUpdateManualAuthorizationIntent(),
            cancellationToken).ConfigureAwait(false);
        if (!resolution.Succeeded || resolution.Request is null)
        {
            return Conflict(new { code = resolution.Error ?? "request_not_authorized" });
        }

        HostUpdateExecutionResult result = await executor.ExecuteAsync(resolution.Request, cancellationToken).ConfigureAwait(false);
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
    /// Attempts recovery for a release currently left in recovery. Recovery accepts only the
    /// route release identity and optional request ID, then reconstructs the exact immutable
    /// request from the durable execution journal before invoking coordinator side effects.
    /// </summary>
    [HttpPost("{releaseId}/recover")]
    [ProducesResponseType(typeof(HostUpdateRecoveryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HostUpdateRecoveryResult>> RecoverAsync(
        string releaseId,
        [FromBody] HostUpdateRecoveryRequestBody? body,
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

        HostUpdateExecutionRequest[] journalRequests = activities.Select(activity => activity.RequestBinding)
            .Where(binding => binding is not null)
            .Select(binding => binding!)
            .ToArray();
        string[] journalBindings = activities.Select(activity => activity.RequestBindingHash)
            .Where(binding => !string.IsNullOrWhiteSpace(binding))
            .Select(binding => binding!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (journalRequests.Length != activities.Count || journalBindings.Length != 1 ||
            journalRequests.Any(binding => !string.Equals(HostUpdateRequestBinding.Compute(binding), journalBindings[0], StringComparison.Ordinal)))
        {
            string code = journalRequests.Length == 0 || journalBindings.Length == 0 ? "recovery_binding_missing" : "recovery_binding_mismatch";
            return Conflict(new { code });
        }

        HostUpdateExecutionRequest failedRequest = journalRequests[0];
        string failedHash = HostUpdateRequestBinding.Compute(failedRequest);
        if (journalRequests.Any(binding => !string.Equals(HostUpdateRequestBinding.Compute(binding), failedHash, StringComparison.Ordinal)))
        {
            return Conflict(new { code = "recovery_binding_mixed" });
        }

        if (!string.IsNullOrWhiteSpace(body?.RequestId) && !string.Equals(body.RequestId, failedRequest.RequestId, StringComparison.Ordinal))
        {
            return Conflict(new { code = "recovery_request_mismatch" });
        }

        HostUpdateRecoveryResult result = await recoveryCoordinator.RecoverAsync(failedRequest, activities, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
