using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.SystemStatus;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Exposes aggregated host, storage, service, and database information for administrators.
/// </summary>
[ApiController]
[Route("api/system")]
[Authorize(Roles = "farm_admin")]
public class SystemInfoController(ISystemInfoService systemInfoService) : ControllerBase
{
    private readonly ISystemInfoService _systemInfoService = systemInfoService;

    /// <summary>
    /// Returns the current system information snapshot for the running PrintFarmer host.
    /// </summary>
    [HttpGet("info")]
    [ProducesResponseType(typeof(SystemInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SystemInfoDto>> GetInfoAsync(CancellationToken cancellationToken)
    {
        SystemInfoDto info = await _systemInfoService.GetSystemInfoAsync(cancellationToken);
        return Ok(info);
    }
}
