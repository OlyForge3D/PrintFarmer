namespace Farm.Infrastructure.Dtos;

/// <summary>
/// A role's current permission grants, joined against the derived permission catalog
/// (#1446) so the role management UI (#1455) can render every enforced permission as
/// absent, granted, or explicitly denied for this role. Returned by
/// <c>GET /api/admin/roles/{roleId}/permissions</c>.
/// </summary>
public record RolePermissionsDto
{
    public required Guid RoleId { get; init; }

    public required string RoleName { get; init; }

    public required string RoleDisplayName { get; init; }

    public required bool IsSystemRole { get; init; }

    /// <summary>
    /// False only for <c>farm_admin</c>, whose access is implicitly total (D6) and cannot be
    /// edited through <c>PUT /api/admin/roles/{roleId}/permissions</c>.
    /// </summary>
    public required bool IsEditable { get; init; }

    /// <summary>
    /// Optimistic concurrency token. Callers must echo this value back on
    /// <c>PUT /api/admin/roles/{roleId}/permissions</c>; a mismatch returns <c>409</c>.
    /// </summary>
    public required DateTime UpdatedAt { get; init; }

    /// <summary>Enforced permissions, grouped by resource, each carrying this role's grant status.</summary>
    public required IReadOnlyList<RolePermissionResourceGroupDto> Resources { get; init; }
}

/// <summary>A single resource and this role's grant status for each permission gating it.</summary>
public record RolePermissionResourceGroupDto
{
    /// <summary>Stable machine key for the resource (e.g. <c>"calibration"</c>, <c>"queue"</c>).</summary>
    public required string Resource { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>Permissions for this resource, sorted alphabetically by action.</summary>
    public required IReadOnlyList<RolePermissionEntryDto> Permissions { get; init; }
}

/// <summary>A single enforced <c>resource:action</c> permission and this role's grant status for it.</summary>
public record RolePermissionEntryDto
{
    /// <summary>Resource key (e.g. <c>"calibration"</c>).</summary>
    public required string Resource { get; init; }

    /// <summary>Action key (e.g. <c>"read"</c>).</summary>
    public required string Action { get; init; }

    /// <summary>Canonical <c>resource:action</c> permission string.</summary>
    public required string Permission { get; init; }

    /// <summary>Human-readable action name from the seeded database catalog, when known.</summary>
    public string? ActionDisplayName { get; init; }

    /// <summary>Human-readable action description from the seeded database catalog, when known.</summary>
    public string? ActionDescription { get; init; }

    /// <summary>Whether <c>{resource}:admin</c> subsumes this permission.</summary>
    public required bool ImpliedByAdmin { get; init; }

    /// <summary>This role's current grant status for this permission.</summary>
    public required RolePermissionGrantStatus Status { get; init; }
}

/// <summary>Tri-state grant status of a single permission for a single role.</summary>
public enum RolePermissionGrantStatus
{
    /// <summary>No <c>RolePermission</c> row exists for this role/permission pair.</summary>
    Absent = 0,

    /// <summary>An explicit <c>RolePermission</c> row grants this permission.</summary>
    Granted = 1,

    /// <summary>An explicit <c>RolePermission</c> row denies this permission (<c>Granted = false</c>).</summary>
    Denied = 2,
}

/// <summary>
/// Full-replacement request body for <c>PUT /api/admin/roles/{roleId}/permissions</c>.
/// </summary>
public record UpdateRolePermissionsRequestDto
{
    /// <summary>
    /// The role's <see cref="RolePermissionsDto.UpdatedAt"/> as last observed by the caller.
    /// A mismatch against the role's current value returns <c>409</c>.
    /// </summary>
    public required DateTime UpdatedAt { get; init; }

    /// <summary>
    /// The complete set of <c>resource:action</c> permissions this role should grant after the
    /// update. Any permission not in this list is removed from the role. Every entry must exist
    /// in the derived permission catalog, or the request is rejected with <c>400</c>.
    /// </summary>
    public required IReadOnlyList<string> Permissions { get; init; }
}

/// <summary>Response for a successful <c>PUT /api/admin/roles/{roleId}/permissions</c>.</summary>
public record UpdateRolePermissionsResponseDto
{
    /// <summary>The role's permissions after the update.</summary>
    public required RolePermissionsDto Role { get; init; }

    /// <summary>
    /// Number of users whose active sessions were revoked as a result of this change (see #1454
    /// for full session-revocation propagation).
    /// </summary>
    public required int RevokedSessionCount { get; init; }
}
