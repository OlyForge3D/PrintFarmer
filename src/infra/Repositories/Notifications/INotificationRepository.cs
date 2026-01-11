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
    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get notification by ID
    /// </summary>
    Task<Notification?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all notifications for a user
    /// </summary>
    Task<IEnumerable<Notification>> GetUserNotificationsAsync(
        Guid userId,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get unread notifications for a user
    /// </summary>
    Task<IEnumerable<Notification>> GetUserUnreadNotificationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get notifications by type
    /// </summary>
    Task<IEnumerable<Notification>> GetByTypeAsync(
        Guid userId,
        NotificationType type,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get notifications for a specific job
    /// </summary>
    Task<IEnumerable<Notification>> GetByJobIdAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark notification as read
    /// </summary>
    Task MarkAsReadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark multiple notifications as read
    /// </summary>
    Task MarkMultipleAsReadAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete notification
    /// </summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all expired notifications for a user
    /// </summary>
    Task DeleteExpiredAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete all notifications older than retention period
    /// </summary>
    Task DeleteOldAsync(int retentionDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get unread notification count for a user
    /// </summary>
    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);
}
