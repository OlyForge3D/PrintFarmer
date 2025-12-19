using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Authentication;

/// <summary>
/// Repository for authentication audit log persistence and queries
/// </summary>
public interface IAuthAuditLogRepository
{
    /// <summary>
    /// Add an audit log entry
    /// </summary>
    Task AddAsync(AuthAuditLog auditLog, CancellationToken cancellationToken = default);

    /// <summary>
    /// Save all pending changes to the database
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get audit logs for a specific user, paginated
    /// </summary>
    Task<List<AuthAuditLog>> GetByUserIdAsync(Guid userId, int pageSize = 50, int pageNumber = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get recent failed login attempts across the system
    /// </summary>
    Task<List<AuthAuditLog>> GetRecentFailedLoginsAsync(int count = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get security events (lockouts, password resets, token revocations)
    /// </summary>
    Task<List<AuthAuditLog>> GetSecurityEventsAsync(DateTime? since = null, int pageSize = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all audit logs for a specific event type
    /// </summary>
    Task<List<AuthAuditLog>> GetByEventTypeAsync(string eventType, int pageSize = 100, int pageNumber = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get audit logs within a date range
    /// </summary>
    Task<List<AuthAuditLog>> GetByDateRangeAsync(DateTime startDate, DateTime endDate, int pageSize = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count total audit log entries
    /// </summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Count failed login attempts for a user/email within a time window
    /// </summary>
    Task<int> CountRecentFailedLoginsAsync(string? usernameOrEmail, TimeSpan timeWindow, CancellationToken cancellationToken = default);
}
