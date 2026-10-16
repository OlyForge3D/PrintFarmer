using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.ReleaseUpdates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.Administration.Controllers.Admin;

/// <summary>
/// Exposes the cached application release update check to farm administrators (issue #3281).
/// </summary>
[ApiController]
[Route("api/admin/release-updates")]
[RequirePermission("system_settings", "admin")]
[Tags("Admin - Release Updates")]
public sealed class ReleaseUpdatesController(IApplicationReleaseUpdateStatusProvider statusProvider) : ControllerBase
{
    private readonly IApplicationReleaseUpdateStatusProvider _statusProvider = statusProvider;

    /// <summary>
    /// Returns the result of the most recent background GitHub release check. This endpoint
    /// only reads in-process cached state and never contacts GitHub itself.
    /// </summary>
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ApplicationReleaseUpdateStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<ApplicationReleaseUpdateStatusDto> GetStatus() => Ok(_statusProvider.GetStatus());
}
