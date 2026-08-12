using System.Security.Claims;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Services.Admin;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers.Admin;

/// <summary>
/// Reads and writes a role's permission grants (#1449), joined against the derived
/// permission catalog (#1446) so the role management UI (#1455) can render a permission
/// matrix per role.
/// </summary>
[ApiController]
[Route("api/admin/roles")]
[RequirePermission("roles", "admin")]
[Tags("Admin - Roles")]
public sealed class RolePermissionsController(IRolePermissionService rolePermissionService) : ControllerBase
{
    private readonly IRolePermissionService _rolePermissionService = rolePermissionService;

    /// <summary>Returns the role's current permission grants joined against the derived catalog.</summary>
    [HttpGet("{roleId:guid}/permissions")]
    [ProducesResponseType(typeof(RolePermissionsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RolePermissionsDto>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken)
    {
        RolePermissionsDto? dto = await _rolePermissionService
            .GetRolePermissionsAsync(roleId, cancellationToken)
            .ConfigureAwait(false);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>
    /// Replaces the role's full permission grant set. Rejects any permission absent from the
    /// derived catalog, edits to <c>farm_admin</c>, stale <c>updatedAt</c> tokens, and changes
    /// that would strip the last active role holding <c>roles:admin</c>/<c>users:admin</c>.
    /// </summary>
    [HttpPut("{roleId:guid}/permissions")]
    [ProducesResponseType(typeof(UpdateRolePermissionsResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UpdateRolePermissionsResponseDto>> UpdatePermissionsAsync(
        Guid roleId,
        [FromBody] UpdateRolePermissionsRequestDto body,
        CancellationToken cancellationToken)
    {
        string? actingUserIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(actingUserIdClaim) || !Guid.TryParse(actingUserIdClaim, out Guid actingUserId))
        {
            return Unauthorized();
        }

        string? ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        RolePermissionUpdateResult result = await _rolePermissionService
            .UpdateRolePermissionsAsync(roleId, body, actingUserId, ipAddress, cancellationToken)
            .ConfigureAwait(false);

        return result switch
        {
            RolePermissionUpdateResult.Success success => Ok(success.Response),
            RolePermissionUpdateResult.RoleNotFound => NotFound(),
            RolePermissionUpdateResult.FarmAdminImmutable => BadRequest(new
            {
                error = "The farm_admin role has implicit total access and cannot be edited.",
            }),
            RolePermissionUpdateResult.InvalidPermissions invalid => BadRequest(new
            {
                error = "One or more permissions are not present in the derived permission catalog.",
                permissions = invalid.Permissions,
            }),
            RolePermissionUpdateResult.ConcurrencyConflict => Conflict(new
            {
                error = "The role was modified by another request. Reload and retry.",
            }),
            RolePermissionUpdateResult.LockoutViolation lockout => Conflict(new
            {
                error = "This change would remove the last active role holding a required administrative permission.",
                permissions = lockout.Permissions,
            }),
            _ => Problem("Unexpected result from role permission update."),
        };
    }
}
