using System.Security.Claims;
using Farm.Infrastructure.Services.Roles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using CreateCustomRoleRequest = Farm.Infrastructure.Contracts.Roles.CreateCustomRoleRequest;
using RoleDetailDto = Farm.Infrastructure.Contracts.Roles.RoleDetailDto;
using RoleHasMembersResponse = Farm.Infrastructure.Contracts.Roles.RoleHasMembersResponse;
using RoleSummaryDto = Farm.Infrastructure.Contracts.Roles.RoleSummaryDto;
using UpdateCustomRoleRequest = Farm.Infrastructure.Contracts.Roles.UpdateCustomRoleRequest;

namespace Farm.Web.Api.Controllers.Admin;

/// <summary>
/// Administrative CRUD for custom roles. System roles (<c>farm_admin</c>, <c>farm_user</c>)
/// can be read but never renamed, deactivated, or deleted through this API. Every mutation is
/// audited and guarded against locking out all administrators (see issue #1448).
/// </summary>
[ApiController]
[Route("api/admin/roles")]
[Authorize(Roles = "farm_admin")]
[Tags("Admin - Roles")]
public class RolesController(IRoleManagementService roleManagementService) : ControllerBase
{
    private readonly IRoleManagementService _roleManagementService = roleManagementService;

    /// <summary>
    /// Lists all roles (system and custom) with member/permission counts.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RoleSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RoleSummaryDto>>> GetRolesAsync(CancellationToken ct)
    {
        IReadOnlyList<RoleSummaryDto> roles = await _roleManagementService.GetRolesAsync(ct);
        return Ok(roles);
    }

    /// <summary>
    /// Gets a single role's full detail, including its resolved permission grants.
    /// </summary>
    [HttpGet("{roleId:guid}", Name = "GetRoleById")]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleDetailDto>> GetRoleAsync(Guid roleId, CancellationToken ct)
    {
        RoleDetailDto? role = await _roleManagementService.GetRoleAsync(roleId, ct);
        return role is null ? NotFound(new { error = $"Role {roleId} was not found." }) : Ok(role);
    }

    /// <summary>
    /// Creates a new custom role. Names must be a unique, lowercase slug and cannot use the
    /// reserved <c>farm_</c> prefix.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RoleDetailDto>> CreateRoleAsync([FromBody] CreateCustomRoleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryGetActingUserId(out Guid actorUserId))
        {
            return Unauthorized();
        }

        try
        {
            RoleDetailDto created = await _roleManagementService.CreateRoleAsync(request, actorUserId, GetIpAddress(), ct);
            return CreatedAtRoute("GetRoleById", new { roleId = created.Id }, created);
        }
        catch (RoleManagementException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.ErrorCode.ToString() });
        }
    }

    /// <summary>
    /// Updates a role's display name, description, or active status. The <c>Name</c> slug is
    /// immutable and system roles can never be deactivated.
    /// </summary>
    [HttpPut("{roleId:guid}")]
    [ProducesResponseType(typeof(RoleDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleDetailDto>> UpdateRoleAsync(Guid roleId, [FromBody] UpdateCustomRoleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryGetActingUserId(out Guid actorUserId))
        {
            return Unauthorized();
        }

        try
        {
            RoleDetailDto updated = await _roleManagementService.UpdateRoleAsync(roleId, request, actorUserId, GetIpAddress(), ct);
            return Ok(updated);
        }
        catch (RoleManagementException ex)
        {
            return MapRoleManagementException(ex);
        }
    }

    /// <summary>
    /// Deletes a custom role. System roles can never be deleted. A role with active members
    /// requires either <paramref name="reassignTo"/> or <paramref name="cascade"/>. Refuses any
    /// deletion that would leave no active admin-equivalent role, or strip the acting
    /// administrator of their own last administrative role.
    /// </summary>
    [HttpDelete("{roleId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(RoleHasMembersResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteRoleAsync(Guid roleId, [FromQuery] Guid? reassignTo, [FromQuery] bool cascade, CancellationToken ct)
    {
        if (!TryGetActingUserId(out Guid actorUserId))
        {
            return Unauthorized();
        }

        try
        {
            await _roleManagementService.DeleteRoleAsync(roleId, reassignTo, cascade, actorUserId, GetIpAddress(), ct);
            return NoContent();
        }
        catch (RoleManagementException ex)
        {
            return MapRoleManagementException(ex);
        }
    }

    private ActionResult MapRoleManagementException(RoleManagementException ex)
    {
        return ex.ErrorCode switch
        {
            RoleManagementErrorCode.NotFound => NotFound(new { error = ex.Message, code = ex.ErrorCode.ToString() }),
            RoleManagementErrorCode.HasMembers => Conflict(new { error = ex.Message, code = ex.ErrorCode.ToString() }),
            _ => BadRequest(new { error = ex.Message, code = ex.ErrorCode.ToString() })
        };
    }

    private bool TryGetActingUserId(out Guid actorUserId)
    {
        string? claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out actorUserId);
    }

    private string? GetIpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
