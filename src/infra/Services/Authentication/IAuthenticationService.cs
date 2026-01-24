using System.Security.Claims;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Service for user authentication, registration, and credential management.
/// Provides JWT token generation, password reset, and email confirmation functionality.
/// </summary>
public interface IAuthenticationService
{
    /// <summary>
    /// Authenticates a user with username and password.
    /// </summary>
    /// <param name="username">The user's username.</param>
    /// <param name="password">The user's password.</param>
    /// <returns>Authentication result with JWT token if successful.</returns>
    Task<AuthenticationResult> AuthenticateAsync(string username, string password);

    /// <summary>
    /// Registers a new user account.
    /// </summary>
    /// <param name="request">Registration request with user details.</param>
    /// <returns>Authentication result with JWT token if registration successful.</returns>
    Task<AuthenticationResult> RegisterAsync(RegisterRequest request);

    /// <summary>
    /// Validates a JWT token.
    /// </summary>
    /// <param name="token">The JWT token to validate.</param>
    /// <returns>True if the token is valid, false otherwise.</returns>
    Task<bool> ValidateTokenAsync(string token);

    /// <summary>
    /// Gets the claims principal from a JWT token.
    /// </summary>
    /// <param name="token">The JWT token.</param>
    /// <returns>ClaimsPrincipal if token is valid, null otherwise.</returns>
    Task<ClaimsPrincipal?> GetPrincipalFromTokenAsync(string token);

    /// <summary>
    /// Generates a new JWT token for a user.
    /// </summary>
    /// <param name="user">The user entity.</param>
    /// <returns>The generated JWT token string.</returns>
    Task<string> GenerateJwtTokenAsync(User user);

    /// <summary>
    /// Sends an email confirmation link to a user.
    /// </summary>
    /// <param name="user">The user to send confirmation to.</param>
    /// <returns>True if email sent successfully.</returns>
    Task<bool> SendEmailConfirmationAsync(User user);

    /// <summary>
    /// Confirms a user's email address using a confirmation token.
    /// </summary>
    /// <param name="token">The email confirmation token.</param>
    /// <returns>True if email confirmed successfully.</returns>
    Task<bool> ConfirmEmailAsync(string token);

    /// <summary>
    /// Initiates a password reset request.
    /// </summary>
    /// <param name="email">The user's email address.</param>
    /// <param name="ipAddress">Optional IP address for audit logging.</param>
    /// <returns>True if reset email sent (or appears sent for security).</returns>
    Task<bool> InitiatePasswordResetAsync(string email, string? ipAddress);

    /// <summary>
    /// Completes a password reset using a reset token.
    /// </summary>
    /// <param name="token">The password reset token.</param>
    /// <param name="email">The user's email address.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="ipAddress">Optional IP address for audit logging.</param>
    /// <returns>True if password reset successfully.</returns>
    Task<bool> ResetPasswordAsync(string token, string email, string newPassword, string? ipAddress);

    /// <summary>
    /// Gets a user with their roles and permissions.
    /// </summary>
    /// <param name="userId">The user's unique identifier.</param>
    /// <returns>User DTO with roles and permissions, or null if not found.</returns>
    Task<UserDto?> GetUserWithRolesAndPermissionsAsync(Guid userId);

    /// <summary>
    /// Checks if a user has a specific permission.
    /// </summary>
    /// <param name="userId">The user's unique identifier.</param>
    /// <param name="resource">The resource to check permission for.</param>
    /// <param name="action">The action to check permission for.</param>
    /// <returns>True if user has the permission.</returns>
    Task<bool> HasPermissionAsync(Guid userId, string resource, string action);

    /// <summary>
    /// Gets a user by username.
    /// </summary>
    /// <param name="username">The username to find.</param>
    /// <returns>The user if found, null otherwise.</returns>
    Task<User?> GetUserByUsernameAsync(string username);

    /// <summary>
    /// Gets a user by email address.
    /// </summary>
    /// <param name="email">The email to find.</param>
    /// <returns>The user if found, null otherwise.</returns>
    Task<User?> GetUserByEmailAsync(string email);

    /// <summary>
    /// Changes a user's password.
    /// </summary>
    /// <param name="userId">The user's unique identifier.</param>
    /// <param name="currentPassword">The current password for verification.</param>
    /// <param name="newPassword">The new password.</param>
    /// <returns>True if password changed successfully.</returns>
    Task<bool> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword);
}
