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
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="jobName">The name of the job.</param>
    /// <param name="printerName">The optional name of the printer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SendJobStartedAsync(string jobId, string jobName, string? printerName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job completed notification
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="jobName">The name of the job.</param>
    /// <param name="printerName">The optional name of the printer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SendJobCompletedAsync(string jobId, string jobName, string? printerName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job failed notification
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="jobName">The name of the job.</param>
    /// <param name="errorMessage">The error message describing the failure.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SendJobFailedAsync(string jobId, string jobName, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job paused notification
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="jobName">The name of the job.</param>
    /// <param name="reason">The optional reason for pausing the job.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SendJobPausedAsync(string jobId, string jobName, string? reason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send job resumed notification
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="jobName">The name of the job.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SendJobResumedAsync(string jobId, string jobName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send custom notification to user
    /// </summary>
    /// <param name="userId">The unique identifier of the user to notify.</param>
    /// <param name="type">The type of notification.</param>
    /// <param name="subject">The notification subject.</param>
    /// <param name="body">The notification body content.</param>
    /// <param name="jobId">The optional job identifier associated with the notification.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task SendNotificationAsync(Guid userId, NotificationType type, string subject, string body, Guid? jobId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get user notifications
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="limit">The optional maximum number of notifications to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<IEnumerable<Notification>> GetUserNotificationsAsync(Guid userId, int? limit = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get unread notifications for user
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<IEnumerable<Notification>> GetUserUnreadNotificationsAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark notification as read
    /// </summary>
    /// <param name="notificationId">The unique identifier of the notification.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task MarkAsReadAsync(string notificationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark multiple notifications as read
    /// </summary>
    /// <param name="notificationIds">The collection of notification identifiers to mark as read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task MarkMultipleAsReadAsync(IEnumerable<string> notificationIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete notification
    /// </summary>
    /// <param name="notificationId">The unique identifier of the notification to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task DeleteAsync(string notificationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get unread notification count
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get user notification preferences
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<NotificationPreferences?> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update user notification preferences
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="preferences">The notification preferences to update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task UpdatePreferencesAsync(Guid userId, NotificationPreferences preferences, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup old notifications based on retention policy
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
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
