namespace Farm.Infrastructure;

// Authentication and User Management DTOs
#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1649 // File name should match first type name

/// <summary>
/// Standard authentication outcome with optional JWT token and error information.
/// </summary>
public record AuthenticationResult(
    bool Success,
    string? Token = null,
    DateTime? ExpiresAt = null,
    Contracts.Auth.UserDto? User = null,
    string? Error = null);

/// <summary>
/// Protected resource entity (authorization domain object).
/// </summary>
public record ResourceDto(
    Guid Id,
    string Name,
    string DisplayName,
    string? Description = null,
    string ResourceType = "",
    bool IsActive = true);

/// <summary>
/// Allowed action within a resource scope.
/// </summary>
public record ActionDto(
    Guid Id,
    string Name,
    string DisplayName,
    string? Description = null);

/// <summary>
/// Granted / denied permission relationship linking role, resource and action.
/// </summary>
public record RolePermissionDto(
    Guid Id,
    Guid RoleId,
    Guid ResourceId,
    Guid ActionId,
    string ResourceName = "",
    string ActionName = "",
    bool Granted = true);

/// <summary>
/// Assignment of a role to a user (with optional expiration).
/// </summary>
public record UserRoleDto(
    Guid Id,
    Guid UserId,
    Guid RoleId,
    string RoleName = "",
    DateTime AssignedAt = default,
    DateTime? ExpiresAt = null,
    bool IsActive = true);

/// <summary>
/// Payload for creating a new user and assigning initial roles.
/// </summary>
public class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public Guid[] RoleIds { get; set; } = [];
}

/// <summary>
/// Partial update for user profile / activation / role membership.
/// </summary>
public class UpdateUserRequest
{
    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public bool? IsActive { get; set; }

    public Guid[]? RoleIds { get; set; }
}

/// <summary>
/// Admin payload for changing another user's password.
/// </summary>
public class AdminChangeUserPasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;

    public string ConfirmNewPassword { get; set; } = string.Empty;
}

/// <summary>
/// Result of a lightweight availability check for prospective username/email.
/// null indicates the value was not requested / provided.
/// </summary>
public record UserAvailabilityDto(bool? UsernameExists, bool? EmailExists);

/// <summary>
/// Payload for creating a new role and its permission set.
/// </summary>
public class CreateRoleRequest
{
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public RolePermissionRequestDto[] Permissions { get; set; } = [];
}

/// <summary>
/// Payload for updating a role's display properties and permissions.
/// </summary>
public class UpdateRoleRequest
{
    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public RolePermissionRequestDto[] Permissions { get; set; } = [];
}

/// <summary>
/// Permission assignment entry within a create/update role request.
/// </summary>
public record RolePermissionRequestDto(
    Guid ResourceId,
    Guid ActionId,
    bool Granted = true);

/// <summary>
/// Confirms a user's email address using a verification token.
/// </summary>
public record ConfirmEmailRequest(string Token);

#pragma warning restore SA1649 // File name should match first type name
#pragma warning restore SA1402 // File may only contain a single type
