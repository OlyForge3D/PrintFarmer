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
    /// <param name="auditLog">The audit log entry to add.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task AddAsync(AuthAuditLog auditLog, CancellationToken cancellationToken = default);

    /// <summary>
    /// Save all pending changes to the database
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get audit logs for a specific user, paginated
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="pageSize">Number of records per page.</param>
    /// <param name="pageNumber">The page number to retrieve (1-based).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<List<AuthAuditLog>> GetByUserIdAsync(Guid userId, int pageSize = 50, int pageNumber = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get recent failed login attempts across the system
    /// </summary>
    /// <param name="count">Maximum number of records to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<List<AuthAuditLog>> GetRecentFailedLoginsAsync(int count = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get security events (lockouts, password resets, token revocations)
    /// </summary>
    /// <param name="since">Optional start date to filter events from.</param>
    /// <param name="pageSize">Number of records per page.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<List<AuthAuditLog>> GetSecurityEventsAsync(DateTime? since = null, int pageSize = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all audit logs for a specific event type
    /// </summary>
    /// <param name="eventType">The event type to filter by.</param>
    /// <param name="pageSize">Number of records per page.</param>
    /// <param name="pageNumber">The page number to retrieve (1-based).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<List<AuthAuditLog>> GetByEventTypeAsync(string eventType, int pageSize = 100, int pageNumber = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get audit logs within a date range
    /// </summary>
    /// <param name="startDate">The start date of the range (inclusive).</param>
    /// <param name="endDate">The end date of the range (inclusive).</param>
    /// <param name="pageSize">Number of records per page.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<List<AuthAuditLog>> GetByDateRangeAsync(DateTime startDate, DateTime endDate, int pageSize = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Count total audit log entries
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Count failed login attempts for a user/email within a time window
    /// </summary>
    /// <param name="usernameOrEmail">The username or email address to check.</param>
    /// <param name="timeWindow">The time window to search within.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    Task<int> CountRecentFailedLoginsAsync(string? usernameOrEmail, TimeSpan timeWindow, CancellationToken cancellationToken = default);
}
