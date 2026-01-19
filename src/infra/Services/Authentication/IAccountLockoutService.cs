namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Service for managing account lockouts after failed login attempts
/// </summary>
public interface IAccountLockoutService
{
    /// <summary>
    /// Check if an account is currently locked out
    /// </summary>
    /// <param name="userId">The user ID to check lockout status for.</param>
    /// <returns>True if the account is locked out; otherwise false.</returns>
    Task<bool> IsLockedOutAsync(Guid userId);

    /// <summary>
    /// Record a failed login attempt and potentially lock the account
    /// </summary>
    /// <param name="userId">The user ID for the failed login attempt.</param>
    /// <param name="identifier">The login identifier used (username or email).</param>
    /// <param name="ipAddress">The IP address of the login attempt.</param>
    /// <param name="failureReason">Optional reason for the login failure.</param>
    Task RecordFailedLoginAsync(Guid userId, string identifier, string? ipAddress, string? failureReason = null);

    /// <summary>
    /// Record a failed login attempt by username (when user doesn't exist)
    /// </summary>
    /// <param name="username">The username that was used in the failed attempt.</param>
    /// <param name="ipAddress">The IP address of the login attempt.</param>
    /// <param name="failureReason">Optional reason for the login failure.</param>
    Task RecordFailedLoginByUsernameAsync(string username, string? ipAddress, string? failureReason = null);

    /// <summary>
    /// Reset failed login count (called after successful login)
    /// </summary>
    /// <param name="userId">The user ID to reset the failed login count for.</param>
    Task ResetFailedLoginCountAsync(Guid userId);

    /// <summary>
    /// Get current failed login count for a user
    /// </summary>
    /// <param name="userId">The user ID to get the failed login count for.</param>
    /// <returns>The number of failed login attempts.</returns>
    Task<int> GetFailedLoginCountAsync(Guid userId);

    /// <summary>
    /// Get the lockout end time for a user
    /// </summary>
    /// <param name="userId">The user ID to get the lockout end time for.</param>
    /// <returns>The lockout end time or null if not locked out.</returns>
    Task<DateTime?> GetLockoutEndAsync(Guid userId);

    /// <summary>
    /// Manually lock an account (admin action)
    /// </summary>
    /// <param name="userId">The user ID to lock.</param>
    /// <param name="lockoutDurationMinutes">Duration to lock the account in minutes.</param>
    Task ManuallyLockAccountAsync(Guid userId, int lockoutDurationMinutes);

    /// <summary>
    /// Unlock an account (admin action)
    /// </summary>
    /// <param name="userId">The user ID to unlock.</param>
    Task UnlockAccountAsync(Guid userId);
}
