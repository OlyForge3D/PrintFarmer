using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Authentication;

/// <summary>
/// Service for logging authentication and authorization events for security audit trail
/// </summary>
public interface IAuthAuditService
{
    /// <summary>
    /// Log a successful login event
    /// </summary>
    /// <param name="userId">The unique identifier of the user who logged in.</param>
    /// <param name="ipAddress">The IP address from which the login occurred.</param>
    /// <param name="userAgent">The user agent string of the client browser or application.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogLoginAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a failed login attempt
    /// </summary>
    /// <param name="usernameOrEmail">The username or email used in the failed login attempt.</param>
    /// <param name="reason">The reason the login failed.</param>
    /// <param name="ipAddress">The IP address from which the login attempt occurred.</param>
    /// <param name="userAgent">The user agent string of the client browser or application.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogLoginFailedAsync(string usernameOrEmail, string reason, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a logout event
    /// </summary>
    /// <param name="userId">The unique identifier of the user who logged out.</param>
    /// <param name="ipAddress">The IP address from which the logout occurred.</param>
    /// <param name="userAgent">The user agent string of the client browser or application.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogLogoutAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a user registration event
    /// </summary>
    /// <param name="userId">The unique identifier of the newly registered user.</param>
    /// <param name="ipAddress">The IP address from which the registration occurred.</param>
    /// <param name="userAgent">The user agent string of the client browser or application.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogRegisterAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a password change event
    /// </summary>
    /// <param name="userId">The unique identifier of the user who changed their password.</param>
    /// <param name="ipAddress">The IP address from which the password change occurred.</param>
    /// <param name="userAgent">The user agent string of the client browser or application.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogPasswordChangeAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a password reset initiation (forgot password)
    /// </summary>
    /// <param name="email">The email address for which password reset was requested.</param>
    /// <param name="ipAddress">The IP address from which the reset was initiated.</param>
    /// <param name="userAgent">The user agent string of the client browser or application.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogPasswordResetInitiatedAsync(string email, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a successful password reset completion
    /// </summary>
    /// <param name="userId">The unique identifier of the user who reset their password.</param>
    /// <param name="ipAddress">The IP address from which the reset was completed.</param>
    /// <param name="userAgent">The user agent string of the client browser or application.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogPasswordResetAsync(Guid userId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log an account lockout event
    /// </summary>
    /// <param name="userId">The unique identifier of the user whose account was locked.</param>
    /// <param name="attemptCount">The number of failed attempts that triggered the lockout.</param>
    /// <param name="lockoutDuration">The duration of the account lockout.</param>
    /// <param name="ipAddress">The IP address from which the lockout-triggering attempt occurred.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogAccountLockedAsync(Guid userId, int attemptCount, TimeSpan lockoutDuration, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log an account unlock event
    /// </summary>
    /// <param name="userId">The unique identifier of the user whose account was unlocked.</param>
    /// <param name="reason">The reason for unlocking the account.</param>
    /// <param name="ipAddress">The IP address from which the unlock was performed.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogAccountUnlockedAsync(Guid userId, string reason, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a refresh token event
    /// </summary>
    /// <param name="userId">The unique identifier of the user refreshing their token.</param>
    /// <param name="ipAddress">The IP address from which the token refresh occurred.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogRefreshTokenAsync(Guid userId, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a token revocation event (force logout)
    /// </summary>
    /// <param name="userId">The unique identifier of the user whose token was revoked.</param>
    /// <param name="revokedByUserId">The unique identifier of the user who performed the revocation.</param>
    /// <param name="reason">The reason for revoking the token.</param>
    /// <param name="ipAddress">The IP address from which the revocation was performed.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogTokenRevokedAsync(Guid userId, Guid revokedByUserId, string reason, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a successful Desktop API key → JWT exchange.
    /// </summary>
    /// <param name="userId">The unique identifier of the API key's owner.</param>
    /// <param name="apiKeyId">The unique identifier of the API key that was exchanged.</param>
    /// <param name="ipAddress">The IP address from which the exchange occurred.</param>
    /// <param name="userAgent">The user agent string of the client application.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogApiKeyExchangeAsync(Guid userId, Guid apiKeyId, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a failed Desktop API key exchange attempt. The reason is a safe, generic
    /// category only (never the raw key, hash, or a distinction that would aid enumeration).
    /// </summary>
    /// <param name="reason">A generic failure category (e.g. "invalid_or_expired_key").</param>
    /// <param name="ipAddress">The IP address from which the exchange attempt occurred.</param>
    /// <param name="userAgent">The user agent string of the client application.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogApiKeyExchangeFailedAsync(string reason, string? ipAddress, string? userAgent, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a role permission grant change (full-replacement) performed via the role
    /// permission API (#1449).
    /// </summary>
    /// <param name="actingUserId">The unique identifier of the farm_admin who made the change.</param>
    /// <param name="roleId">The unique identifier of the role whose permissions changed.</param>
    /// <param name="roleName">The machine name of the role whose permissions changed.</param>
    /// <param name="permissionsAdded">Permissions granted to the role that were not previously granted.</param>
    /// <param name="permissionsRemoved">Permissions no longer granted to the role.</param>
    /// <param name="revokedSessionCount">Number of users whose active sessions were revoked as a result.</param>
    /// <param name="ipAddress">The IP address from which the change was made.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogRolePermissionsChangedAsync(
        Guid actingUserId,
        Guid roleId,
        string roleName,
        IReadOnlyList<string> permissionsAdded,
        IReadOnlyList<string> permissionsRemoved,
        int revokedSessionCount,
        string? ipAddress,
        string? correlationId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a role management mutation (create, update, or delete) for the audit trail.
    /// </summary>
    /// <param name="actorUserId">The unique identifier of the administrator performing the change.</param>
    /// <param name="roleId">The unique identifier of the affected role.</param>
    /// <param name="roleName">The name (slug) of the affected role.</param>
    /// <param name="eventType">Must be one of <see cref="AuthEventType.RoleCreated"/>, <see cref="AuthEventType.RoleUpdated"/>, or <see cref="AuthEventType.RoleDeleted"/>.</param>
    /// <param name="beforeJson">Optional JSON snapshot of the role state before the change.</param>
    /// <param name="afterJson">Optional JSON snapshot of the role state after the change.</param>
    /// <param name="ipAddress">The IP address from which the change was made.</param>
    /// <param name="correlationId">Optional correlation identifier for request tracing.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task LogRoleManagementEventAsync(Guid actorUserId, Guid roleId, string roleName, AuthEventType eventType, string? beforeJson, string? afterJson, string? ipAddress, string? correlationId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get audit log entries for a specific user
    /// </summary>
    /// <param name="userId">The unique identifier of the user to retrieve audit logs for.</param>
    /// <param name="pageSize">The number of entries per page.</param>
    /// <param name="pageNumber">The page number to retrieve (1-based).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<List<AuthAuditLog>> GetUserAuditLogAsync(Guid userId, int pageSize = 50, int pageNumber = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get recent failed login attempts across the system
    /// </summary>
    /// <param name="count">The maximum number of failed login entries to retrieve.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<List<AuthAuditLog>> GetRecentFailedLoginsAsync(int count = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get security events (lockouts, password resets, token revocations)
    /// </summary>
    /// <param name="since">Optional filter to retrieve events after this date and time.</param>
    /// <param name="pageSize">The maximum number of security events to retrieve.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<List<AuthAuditLog>> GetSecurityEventsAsync(DateTime? since = null, int pageSize = 100, CancellationToken cancellationToken = default);
}
