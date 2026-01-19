using Farm.Infrastructure.Domain.Notifications;

namespace Farm.Infrastructure.Repositories.Notifications;

/// <summary>
/// Repository for managing notifications
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    /// Add a new notification
    /// </summary>
    /// <param name="notification">The notification to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get notification by ID
    /// </summary>
    /// <param name="id">The notification identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Notification?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all notifications for a user
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="limit">Optional limit on the number of notifications to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IEnumerable<Notification>> GetUserNotificationsAsync(
        Guid userId,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get unread notifications for a user
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IEnumerable<Notification>> GetUserUnreadNotificationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get notifications by type
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="type">The notification type to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IEnumerable<Notification>> GetByTypeAsync(
        Guid userId,
        NotificationType type,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get notifications for a specific job
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IEnumerable<Notification>> GetByJobIdAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark notification as read
    /// </summary>
    /// <param name="id">The notification identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MarkAsReadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark multiple notifications as read
    /// </summary>
    /// <param name="ids">The notification identifiers to mark as read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MarkMultipleAsReadAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete notification
    /// </summary>
    /// <param name="id">The notification identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all expired notifications for a user
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteExpiredAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all notifications older than retention period
    /// </summary>
    /// <param name="retentionDays">The number of days to retain notifications.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteOldAsync(int retentionDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get unread notification count for a user
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);
}
