using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Services.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Admin;

/// <summary>
/// Exposes the permission catalog derived from routed endpoint metadata, so the role
/// management UI (#1455) can render a permission matrix without hardcoding the permission
/// list.
/// </summary>
[ApiController]
[Route("api/admin/permissions")]
[RequirePermission("roles", "admin")]
[Tags("Admin - Permissions")]
public sealed class PermissionCatalogController(IPermissionCatalogService permissionCatalogService) : ControllerBase
{
    private readonly IPermissionCatalogService _permissionCatalogService = permissionCatalogService;

    /// <summary>
    /// Returns every permission actually enforced by a routed endpoint, grouped by resource,
    /// plus any database catalog rows that no endpoint enforces.
    /// </summary>
    [HttpGet("catalog")]
    [ProducesResponseType(typeof(PermissionCatalogDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PermissionCatalogDto>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        PermissionCatalogDto catalog = await _permissionCatalogService
            .GetCatalogAsync(cancellationToken)
            .ConfigureAwait(false);
        return Ok(catalog);
    }
}
