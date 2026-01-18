using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Contracts.Auth;

/// <summary>
/// Request to register a new user account
/// </summary>
public class RegisterRequest
{
    [Required]
    [StringLength(50, MinimumLength = 3)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [StringLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(255, MinimumLength = 8)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(Password))]
    public string ConfirmPassword { get; set; } = string.Empty;

    [StringLength(100)]
    public string? FirstName { get; set; }

    [StringLength(100)]
    public string? LastName { get; set; }
}

/// <summary>
/// Request to authenticate and obtain access tokens
/// </summary>
public class LoginRequest
{
    [Required]
    public string UsernameOrEmail { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
}

/// <summary>
/// Response containing authentication tokens and user information
/// </summary>
public class LoginResponse
{
    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public DateTime AccessTokenExpires { get; set; }

    public DateTime RefreshTokenExpires { get; set; }

    public UserDto User { get; set; } = null!;
}

/// <summary>
/// Request to refresh an expired access token
/// </summary>
public class RefreshTokenRequest
{
    [Required]
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>
/// Response with new access and refresh tokens
/// </summary>
public class RefreshTokenResponse
{
    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public DateTime AccessTokenExpires { get; set; }

    public DateTime RefreshTokenExpires { get; set; }
}

/// <summary>
/// Request to change user password
/// </summary>
public class ChangePasswordRequest
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [StringLength(255, MinimumLength = 8)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(NewPassword))]
    public string ConfirmNewPassword { get; set; } = string.Empty;
}

/// <summary>
/// Request to initiate password reset process
/// </summary>
public class ForgotPasswordRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// Response to forgot password request
/// </summary>
public class ForgotPasswordResponse
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Request to reset password with token
/// </summary>
public class ResetPasswordRequest
{
    [Required]
    public string Token { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [StringLength(255, MinimumLength = 8)]
    public string NewPassword { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(NewPassword))]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>
/// Response to reset password request
/// </summary>
public class ResetPasswordResponse
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// User data transfer object
/// </summary>
public class UserDto
{
    public Guid Id { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public bool IsActive { get; set; }

    public bool EmailConfirmed { get; set; }

    public DateTime? LastLogin { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<string> Roles { get; set; } = new();

    public List<string> Permissions { get; set; } = new();
}

/// <summary>
/// Role data transfer object
/// </summary>
public class RoleDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsSystemRole { get; set; }

    public bool IsActive { get; set; }

    public List<PermissionDto> Permissions { get; set; } = new();
}

/// <summary>
/// Permission data transfer object (resource:action format)
/// </summary>
public class PermissionDto
{
    public string Resource { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public bool Granted { get; set; }

    /// <summary>
    /// Returns permission in resource:action format (e.g., "printers:create")
    /// </summary>
    public string ToPermissionString() => $"{Resource}:{Action}";
}
