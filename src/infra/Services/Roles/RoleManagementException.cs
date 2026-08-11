namespace Farm.Infrastructure.Services.Roles;

/// <summary>
/// Specific, machine-checkable reasons a role mutation was refused. Mirrors the D6/D7/D8/D9
/// invariants from issue #1448 so callers (and tests) can assert on the exact violated rule
/// rather than a generic error string.
/// </summary>
public enum RoleManagementErrorCode
{
    NotFound,

    /// <summary>D7 — name failed the slug pattern, is not unique, or uses the reserved farm_ prefix.</summary>
    InvalidName,

    /// <summary>D7 — a PUT attempted to change the immutable Name.</summary>
    NameIsImmutable,

    /// <summary>D6 — system roles cannot be renamed, deleted, or deactivated.</summary>
    SystemRoleProtected,

    /// <summary>D8 — delete was attempted on a role that still has members, without reassignment or cascade.</summary>
    HasMembers,

    /// <summary>Reassignment target role does not exist or is not a valid destination.</summary>
    InvalidReassignmentTarget,

    /// <summary>D9 — this mutation would leave no active role granting roles:admin + users:admin to any active user.</summary>
    LastAdminRole,

    /// <summary>D9 — the acting administrator would lose their own last administrative role.</summary>
    SelfLockout,

    /// <summary>Referenced a resource:action permission pair or copy-from role that does not exist.</summary>
    InvalidPermission
}

/// <summary>
/// Thrown when a role CRUD operation is refused because it would violate one of the
/// system-role-protection or admin-lockout invariants.
/// </summary>
public class RoleManagementException : Exception
{
    public RoleManagementErrorCode ErrorCode { get; }

    public RoleManagementException(RoleManagementErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public RoleManagementException(string message)
        : base(message)
    {
        ErrorCode = RoleManagementErrorCode.InvalidName;
    }

    public RoleManagementException(string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = RoleManagementErrorCode.InvalidName;
    }

    public RoleManagementException()
    {
        ErrorCode = RoleManagementErrorCode.InvalidName;
    }
}
