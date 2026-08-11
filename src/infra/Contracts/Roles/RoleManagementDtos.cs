using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Contracts.Roles;

/// <summary>
/// Summary view of a role for list endpoints. Excludes the full permission set for
/// payload efficiency; use <see cref="RoleDetailDto"/> for a single role's full grants.
/// </summary>
public class RoleSummaryDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsSystemRole { get; set; }

    public bool IsActive { get; set; }

    public int MemberCount { get; set; }

    public int PermissionCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Full detail view of a single role, including its complete permission set.
/// </summary>
public class RoleDetailDto : RoleSummaryDto
{
    public List<Contracts.Auth.PermissionDto> Permissions { get; set; } = new();
}

/// <summary>
/// Request to create a new custom role. Always results in <c>IsSystemRole = false</c>.
/// </summary>
public class CreateCustomRoleRequest
{
    /// <summary>
    /// Immutable slug used in the JWT <c>role</c> claim. Must match
    /// <c>^[a-z][a-z0-9_]{2,49}$</c>, be unique case-insensitively, and must not use the
    /// reserved <c>farm_</c> prefix.
    /// </summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Optional initial permissions in <c>resource:action</c> format (e.g. <c>printers:read</c>).
    /// Ignored if <see cref="CopyFromRoleId"/> is set.
    /// </summary>
    public List<string>? Permissions { get; set; }

    /// <summary>
    /// Optional role ID to clone the permission set from. When set, <see cref="Permissions"/>
    /// is ignored and the new role's grants are copied from the source role.
    /// </summary>
    public Guid? CopyFromRoleId { get; set; }
}

/// <summary>
/// Request to update an existing role. <c>Name</c> is intentionally absent — role names are
/// immutable once created (they are embedded in live JWTs).
/// </summary>
public class UpdateCustomRoleRequest
{
    /// <summary>
    /// If provided and different from the current stored value, the request is rejected —
    /// this field exists only so a client that echoes the full DTO back does not
    /// accidentally attempt a silent rename.
    /// </summary>
    public string? Name { get; set; }

    [StringLength(100, MinimumLength = 1)]
    public string? DisplayName { get; set; }

    [StringLength(1000)]
    public string? Description { get; set; }

    public bool? IsActive { get; set; }
}

/// <summary>
/// Response returned when a role delete is refused because it still has members.
/// </summary>
public class RoleHasMembersResponse
{
    public string Error { get; set; } = string.Empty;

    public int MemberCount { get; set; }
}
