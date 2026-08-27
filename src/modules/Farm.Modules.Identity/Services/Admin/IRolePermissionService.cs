using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure.Dtos;

namespace Farm.Modules.Identity.Services.Admin;

/// <summary>
/// Reads and writes a role's permission grants, joined against the derived permission
/// catalog (#1446). Backs <c>GET</c>/<c>PUT /api/admin/roles/{roleId}/permissions</c>.
/// </summary>
public interface IRolePermissionService
{
    /// <summary>
    /// Returns the role's current permission grants joined against the derived catalog, or
    /// <see langword="null"/> if the role does not exist.
    /// </summary>
    Task<RolePermissionsDto?> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the role's full permission grant set. See <see cref="RolePermissionUpdateResult"/>
    /// for the possible outcomes.
    /// </summary>
    /// <param name="roleId">The role to update.</param>
    /// <param name="request">The full-replacement request body.</param>
    /// <param name="actingUserId">The farm_admin making the change, for audit/revocation attribution.</param>
    /// <param name="ipAddress">The IP address the change was made from, for audit attribution.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<RolePermissionUpdateResult> UpdateRolePermissionsAsync(
        Guid roleId,
        UpdateRolePermissionsRequestDto request,
        Guid actingUserId,
        string? ipAddress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Closed set of outcomes for <see cref="IRolePermissionService.UpdateRolePermissionsAsync"/>,
/// so the controller can map each case to the correct HTTP status without relying on
/// exceptions for control flow.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1034:Nested types should not be visible",
    Justification = "Nested result-case records keep the closed outcome set discoverable and exhaustively pattern-matchable at the call site.")]
public abstract record RolePermissionUpdateResult
{
    private RolePermissionUpdateResult()
    {
    }

    /// <summary>The role's permissions were replaced successfully.</summary>
    public sealed record Success(UpdateRolePermissionsResponseDto Response) : RolePermissionUpdateResult;

    /// <summary>No role exists with the given id.</summary>
    public sealed record RoleNotFound : RolePermissionUpdateResult;

    /// <summary>The target role is <c>farm_admin</c>, whose access is implicitly total (D6) and cannot be edited.</summary>
    public sealed record FarmAdminImmutable : RolePermissionUpdateResult;

    /// <summary>One or more requested permissions are not present in the derived permission catalog.</summary>
    public sealed record InvalidPermissions(IReadOnlyList<string> Permissions) : RolePermissionUpdateResult;

    /// <summary>The role was modified since the caller last observed <c>UpdatedAt</c>.</summary>
    public sealed record ConcurrencyConflict : RolePermissionUpdateResult;

    /// <summary>
    /// The requested change would remove the last active role holding a lockout-guarded
    /// permission (D9: <c>roles:admin</c> / <c>users:admin</c>).
    /// </summary>
    public sealed record LockoutViolation(IReadOnlyList<string> Permissions) : RolePermissionUpdateResult;
}
