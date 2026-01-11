using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Notifications;

/// <summary>
/// Service for managing user notifications
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Send job started notification
    /// </summary>
    Task SendJobStartedAsync(string jobId, string jobName, string? printerName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job completed notification
    /// </summary>
    Task SendJobCompletedAsync(string jobId, string jobName, string? printerName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job failed notification
    /// </summary>
    Task SendJobFailedAsync(string jobId, string jobName, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job paused notification
    /// </summary>
    Task SendJobPausedAsync(string jobId, string jobName, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job resumed notification
    /// </summary>
    Task SendJobResumedAsync(string jobId, string jobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send custom notification to user
    /// </summary>
    Task SendNotificationAsync(Guid userId, NotificationType type, string subject, string body, Guid? jobId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get user notifications
    /// </summary>
    Task<IEnumerable<Notification>> GetUserNotificationsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get unread notifications for user
    /// </summary>
    Task<IEnumerable<Notification>> GetUserUnreadNotificationsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark notification as read
    /// </summary>
    Task MarkAsReadAsync(string notificationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark multiple notifications as read
    /// </summary>
    Task MarkMultipleAsReadAsync(IEnumerable<string> notificationIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete notification
    /// </summary>
    Task DeleteAsync(string notificationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get unread notification count
    /// </summary>
    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get user notification preferences
    /// </summary>
    Task<NotificationPreferences?> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update user notification preferences
    /// </summary>
    Task UpdatePreferencesAsync(Guid userId, NotificationPreferences preferences, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup old notifications based on retention policy
    /// </summary>
    Task CleanupOldNotificationsAsync(CancellationToken cancellationToken = default);
}

public class NotificationService(
    INotificationRepository notificationRepository,
    ILogger<NotificationService> logger) : INotificationService
{
    public Task SendJobStartedAsync(
        string jobId,
        string jobName,
        string? printerName = null,
        CancellationToken cancellationToken = default)
    {
        // Note: In Phase 4.3, we don't know who created the job yet
        // This will be populated once we integrate with PrintQueueService
        // For now, this is a placeholder
        logger.LogInformation("Job started notification queued for job {JobId}: {JobName}", jobId, jobName);
        return Task.CompletedTask;
    }

    public Task SendJobCompletedAsync(
        string jobId,
        string jobName,
        string? printerName = null,
        CancellationToken cancellationToken = default)
    {
        // Note: In Phase 4.3, we don't know who created the job yet
        // This will be populated once we integrate with PrintQueueService
        logger.LogInformation("Job completed notification queued for job {JobId}: {JobName}", jobId, jobName);
        return Task.CompletedTask;
    }

    public Task SendJobFailedAsync(
        string jobId,
        string jobName,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        // Note: In Phase 4.3, we don't know who created the job yet
        logger.LogInformation("Job failed notification queued for job {JobId}: {JobName} - Error: {Error}", jobId, jobName, errorMessage);
        return Task.CompletedTask;
    }

    public Task SendJobPausedAsync(
        string jobId,
        string jobName,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Job paused notification queued for job {JobId}: {JobName}", jobId, jobName);
        return Task.CompletedTask;
    }

    public Task SendJobResumedAsync(
        string jobId,
        string jobName,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Job resumed notification queued for job {JobId}: {JobName}", jobId, jobName);
        return Task.CompletedTask;
    }

    public async Task SendNotificationAsync(
        Guid userId,
        NotificationType type,
        string subject,
        string body,
        Guid? jobId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var notification = new Notification
            {
                UserId = userId,
                JobId = jobId,
                Type = type,
                Subject = subject,
                Body = body,
                CreatedAt = DateTime.UtcNow
            };

            await notificationRepository.AddAsync(notification, cancellationToken);
            logger.LogInformation("Notification sent to user {UserId}: {Subject}", userId, subject);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending notification to user {UserId}", userId);
            throw;
        }
    }

    public async Task<IEnumerable<Notification>> GetUserNotificationsAsync(
        Guid userId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return await notificationRepository.GetUserNotificationsAsync(userId, limit, cancellationToken);
    }

    public async Task<IEnumerable<Notification>> GetUserUnreadNotificationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await notificationRepository.GetUserUnreadNotificationsAsync(userId, cancellationToken);
    }

    public async Task MarkAsReadAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        await notificationRepository.MarkAsReadAsync(notificationId, cancellationToken);
        logger.LogInformation("Notification {NotificationId} marked as read", notificationId);
    }

    public async Task MarkMultipleAsReadAsync(IEnumerable<string> notificationIds, CancellationToken cancellationToken = default)
    {
        await notificationRepository.MarkMultipleAsReadAsync(notificationIds, cancellationToken);
        logger.LogInformation("Marked multiple notifications as read");
    }

    public async Task DeleteAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        await notificationRepository.DeleteAsync(notificationId, cancellationToken);
        logger.LogInformation("Notification {NotificationId} deleted", notificationId);
    }

    public async Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await notificationRepository.GetUnreadCountAsync(userId, cancellationToken);
    }

    public Task<NotificationPreferences?> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // TODO: Implement after creating NotificationPreferencesRepository
        logger.LogInformation("Get preferences for user {UserId}", userId);
        return Task.FromResult<NotificationPreferences?>(null);
    }

    public Task UpdatePreferencesAsync(Guid userId, NotificationPreferences preferences, CancellationToken cancellationToken = default)
    {
        // TODO: Implement after creating NotificationPreferencesRepository
        logger.LogInformation("Update preferences for user {UserId}", userId);
        return Task.CompletedTask;
    }

    public async Task CleanupOldNotificationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Delete notifications older than default retention (30 days)
            await notificationRepository.DeleteOldAsync(30, cancellationToken);
            logger.LogInformation("Notification cleanup completed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during notification cleanup");
        }
    }
}
