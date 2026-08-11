using Farm.Infrastructure.Contracts.Roles;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Roles;

/// <summary>
/// Repository interface for role CRUD operations and the queries needed to enforce
/// system-role protection and admin-lockout guardrails.
/// </summary>
public interface IRolesRepository
{
    Task<List<RoleSummaryDto>> GetRoleSummariesAsync(CancellationToken ct = default);

    Task<Role?> GetRoleEntityAsync(Guid id, CancellationToken ct = default);

    Task<RoleDetailDto?> GetRoleDetailAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Checks whether a role name already exists (case-insensitive), optionally excluding
    /// one role ID (for update scenarios, though names are otherwise immutable).
    /// </summary>
    Task<bool> NameExistsAsync(string name, Guid? excludeRoleId = null, CancellationToken ct = default);

    Task AddRoleAsync(Role role, CancellationToken ct = default);

    /// <summary>
    /// Resolves a "resource:action" pair to the underlying Resource/Action IDs. Returns null
    /// if either side does not exist.
    /// </summary>
    Task<(Guid ResourceId, Guid ActionId)?> ResolvePermissionAsync(string resource, string action, CancellationToken ct = default);

    Task AddRolePermissionsAsync(Guid roleId, IEnumerable<(Guid ResourceId, Guid ActionId)> pairs, CancellationToken ct = default);

    /// <summary>
    /// Copies all granted permissions from one role to another (used by
    /// <see cref="Contracts.Roles.CreateCustomRoleRequest.CopyFromRoleId"/>).
    /// </summary>
    Task CopyRolePermissionsAsync(Guid sourceRoleId, Guid targetRoleId, CancellationToken ct = default);

    Task<int> CountActiveMembersAsync(Guid roleId, CancellationToken ct = default);

    /// <summary>
    /// Reassigns all active memberships of <paramref name="fromRoleId"/> to
    /// <paramref name="toRoleId"/>, preserving each membership's <c>ExpiresAt</c>. If a user
    /// already holds the target role, their old assignment is simply removed instead of
    /// creating a duplicate (unique on UserId+RoleId).
    /// </summary>
    Task ReassignMembersAsync(Guid fromRoleId, Guid toRoleId, CancellationToken ct = default);

    /// <summary>
    /// Removes all UserRole rows for the given role (cascade delete path).
    /// </summary>
    Task RemoveMembersAsync(Guid roleId, CancellationToken ct = default);

    Task DeleteRoleAsync(Role role, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// True if the role is active and grants both <c>roles:admin</c> and <c>users:admin</c>
    /// (or is the <c>farm_admin</c> system role, which is admin-equivalent by definition).
    /// </summary>
    Task<bool> IsAdminEquivalentAsync(Guid roleId, CancellationToken ct = default);

    /// <summary>
    /// True if at least one other active role (excluding <paramref name="excludeRoleId"/>) is
    /// admin-equivalent and has at least one active, enabled user assigned to it.
    /// </summary>
    Task<bool> HasOtherActiveAdminCoverageAsync(Guid excludeRoleId, CancellationToken ct = default);

    /// <summary>
    /// True if the given user currently holds another active, unexpired, admin-equivalent
    /// role besides <paramref name="excludeRoleId"/>.
    /// </summary>
    Task<bool> UserHasOtherActiveAdminEquivalentRoleAsync(Guid userId, Guid excludeRoleId, CancellationToken ct = default);

    /// <summary>
    /// True if the given user currently holds an active, unexpired membership in the given role.
    /// </summary>
    Task<bool> UserIsActiveMemberOfRoleAsync(Guid userId, Guid roleId, CancellationToken ct = default);
}
