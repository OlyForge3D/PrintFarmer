using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Admin;

/// <summary>
/// Admin Control Center overview endpoint. Aggregates the existing health-check pipeline
/// into a single response the <c>/admin</c> hub renders in one call.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = "farm_admin")]
[Tags("Admin - Overview")]
public sealed class AdminOverviewController(IAdminOverviewService overviewService) : ControllerBase
{
    private readonly IAdminOverviewService _overviewService = overviewService;

    /// <summary>
    /// Returns a subsystem-health snapshot plus a ranked list of items needing attention.
    /// The endpoint aggregates existing health-check results; it does not run new probes.
    /// If any individual subsystem check fails or times out, the response still returns
    /// with that subsystem marked <c>Unknown</c> instead of failing the whole endpoint.
    /// </summary>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(AdminOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AdminOverviewDto>> GetOverviewAsync(CancellationToken cancellationToken)
    {
        AdminOverviewDto overview = await _overviewService.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        return Ok(overview);
    }
}
