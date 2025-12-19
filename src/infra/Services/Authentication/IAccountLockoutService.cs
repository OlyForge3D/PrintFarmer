namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Service for managing account lockouts after failed login attempts
/// </summary>
public interface IAccountLockoutService
{
    /// <summary>
    /// Check if an account is currently locked out
    /// </summary>
    Task<bool> IsLockedOutAsync(Guid userId);

    /// <summary>
    /// Record a failed login attempt and potentially lock the account
    /// </summary>
    Task RecordFailedLoginAsync(Guid userId, string identifier, string? ipAddress, string? failureReason = null);

    /// <summary>
    /// Record a failed login attempt by username (when user doesn't exist)
    /// </summary>
    Task RecordFailedLoginByUsernameAsync(string username, string? ipAddress, string? failureReason = null);

    /// <summary>
    /// Reset failed login count (called after successful login)
    /// </summary>
    Task ResetFailedLoginCountAsync(Guid userId);

    /// <summary>
    /// Get current failed login count for a user
    /// </summary>
    Task<int> GetFailedLoginCountAsync(Guid userId);

    /// <summary>
    /// Get the lockout end time for a user
    /// </summary>
    Task<DateTime?> GetLockoutEndAsync(Guid userId);

    /// <summary>
    /// Manually lock an account (admin action)
    /// </summary>
    Task ManuallyLockAccountAsync(Guid userId, int lockoutDurationMinutes);

    /// <summary>
    /// Unlock an account (admin action)
    /// </summary>
    Task UnlockAccountAsync(Guid userId);
}
