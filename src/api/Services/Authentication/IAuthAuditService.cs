using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Authentication;

/// <summary>
/// Service for logging authentication and authorization events for security audit trail
/// </summary>
public interface IAuthAuditService
{
    /// <summary>
    /// Log a successful login event
    /// </summary>
    Task LogLoginAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a failed login attempt
    /// </summary>
    Task LogLoginFailedAsync(string usernameOrEmail, string reason, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a logout event
    /// </summary>
    Task LogLogoutAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a user registration event
    /// </summary>
    Task LogRegisterAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a password change event
    /// </summary>
    Task LogPasswordChangeAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a password reset initiation (forgot password)
    /// </summary>
    Task LogPasswordResetInitiatedAsync(string email, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a successful password reset completion
    /// </summary>
    Task LogPasswordResetAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log an account lockout event
    /// </summary>
    Task LogAccountLockedAsync(Guid userId, int attemptCount, TimeSpan lockoutDuration, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log an account unlock event
    /// </summary>
    Task LogAccountUnlockedAsync(Guid userId, string reason, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a refresh token event
    /// </summary>
    Task LogRefreshTokenAsync(Guid userId, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a token revocation event (force logout)
    /// </summary>
    Task LogTokenRevokedAsync(Guid userId, Guid revokedByUserId, string reason, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get audit log entries for a specific user
    /// </summary>
    Task<List<AuthAuditLog>> GetUserAuditLogAsync(Guid userId, int pageSize = 50, int pageNumber = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get recent failed login attempts across the system
    /// </summary>
    Task<List<AuthAuditLog>> GetRecentFailedLoginsAsync(int count = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get security events (lockouts, password resets, token revocations)
    /// </summary>
    Task<List<AuthAuditLog>> GetSecurityEventsAsync(DateTime? since = null, int pageSize = 100, CancellationToken cancellationToken = default);
}
