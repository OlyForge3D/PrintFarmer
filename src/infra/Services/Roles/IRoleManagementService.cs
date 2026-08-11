using Farm.Infrastructure.Contracts.Roles;

namespace Farm.Infrastructure.Services.Roles;

/// <summary>
/// Business logic for role CRUD: name validation, system-role protection, and the D9
/// admin-lockout guardrails. All mutation methods throw <see cref="RoleManagementException"/>
/// with a specific <see cref="RoleManagementErrorCode"/> when a request is refused.
/// </summary>
public interface IRoleManagementService
{
    Task<IReadOnlyList<RoleSummaryDto>> GetRolesAsync(CancellationToken ct = default);

    Task<RoleDetailDto?> GetRoleAsync(Guid roleId, CancellationToken ct = default);

    /// <summary>
    /// Creates a new custom role. Always sets <c>IsSystemRole = false</c>.
    /// </summary>
    Task<RoleDetailDto> CreateRoleAsync(CreateCustomRoleRequest request, Guid actorUserId, string? ipAddress, CancellationToken ct = default);

    /// <summary>
    /// Updates DisplayName/Description/IsActive for an existing role. Rejects any attempt to
    /// change Name, and rejects deactivation of system roles or of the last admin-equivalent role.
    /// </summary>
    Task<RoleDetailDto> UpdateRoleAsync(Guid roleId, UpdateCustomRoleRequest request, Guid actorUserId, string? ipAddress, CancellationToken ct = default);

    /// <summary>
    /// Deletes a custom role. System roles can never be deleted. A role with active members is
    /// only deleted when <paramref name="reassignToRoleId"/> or <paramref name="cascade"/> is supplied.
    /// </summary>
    Task DeleteRoleAsync(Guid roleId, Guid? reassignToRoleId, bool cascade, Guid actorUserId, string? ipAddress, CancellationToken ct = default);
}
