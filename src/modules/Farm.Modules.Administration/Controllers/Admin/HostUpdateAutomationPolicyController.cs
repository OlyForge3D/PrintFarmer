using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Services.HostUpdates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.Administration.Controllers.Admin;

public sealed record HostUpdateAutomationPolicyRequest(
    long ExpectedRevision,
    bool Enabled,
    bool KillSwitch,
    string Channel,
    bool InsiderAcknowledged,
    int PollIntervalSeconds,
    int? InsiderPollIntervalSeconds,
    int MaintenanceWindowStartHour,
    int MaintenanceWindowEndHour);

/// <summary>Farm-admin-only CAS API for the durable standing host-update policy.</summary>
[ApiController]
[Route("api/admin/host-updates/automation-policy")]
[RequirePermission("system_settings", "admin")]
[Tags("Admin - Host Updates")]
public sealed class HostUpdateAutomationPolicyController(IHostUpdateAutomationPolicyRepository repository) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(HostUpdateAutomationPolicy), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<HostUpdateAutomationPolicy> Get()
    {
        HostUpdatePolicyReadResult result = repository.Read();
        return result.Available ? Ok(result.Policy) : StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = result.Error });
    }

    [HttpPut]
    [ProducesResponseType(typeof(HostUpdateAutomationPolicy), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HostUpdateAutomationPolicy>> ReplaceAsync(
        [FromBody] HostUpdateAutomationPolicyRequest request,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Channel))
        {
            return BadRequest(new { code = "policy_invalid" });
        }

        HostUpdateAutomationPolicy candidate = new(request.Enabled, request.KillSwitch, request.Channel, request.InsiderAcknowledged, request.PollIntervalSeconds, request.InsiderPollIntervalSeconds, request.MaintenanceWindowStartHour, request.MaintenanceWindowEndHour);
        HostUpdatePolicyReadResult result = await repository.ReplaceAsync(candidate, request.ExpectedRevision, ct).ConfigureAwait(false);
        if (result.Error == "host_update_policy_revision_conflict")
        {
            return Conflict(new { code = result.Error, current = result.Policy });
        }

        if (!result.Available)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = result.Error });
        }

        return Ok(result.Policy);
    }
}
