using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Webhooks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
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
    IUsersRepository usersRepository,
    ILogger<NotificationService> logger,
    AppDbContext dbContext,
    IHubContext<PrinterHub>? hubContext = null,
    IWebhookService? webhookService = null) : INotificationService
{
    public async Task SendJobStartedAsync(
        string jobId,
        string jobName,
        string? printerName = null,
        CancellationToken cancellationToken = default)
    {
        string subject = printerName != null
            ? $"Job started on {printerName}"
            : "Job started";
        string body = printerName != null
            ? $"Print job \"{jobName}\" has started printing on {printerName}."
            : $"Print job \"{jobName}\" has started printing.";

        await BroadcastJobNotificationAsync(
            NotificationType.JobStarted, subject, body, jobId, cancellationToken);
    }

    public async Task SendJobCompletedAsync(
        string jobId,
        string jobName,
        string? printerName = null,
        CancellationToken cancellationToken = default)
    {
        string subject = printerName != null
            ? $"Job completed on {printerName}"
            : "Job completed";
        string body = printerName != null
            ? $"Print job \"{jobName}\" has completed successfully on {printerName}."
            : $"Print job \"{jobName}\" has completed successfully.";

        await BroadcastJobNotificationAsync(
            NotificationType.JobCompleted, subject, body, jobId, cancellationToken);
    }

    public async Task SendJobFailedAsync(
        string jobId,
        string jobName,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        string subject = "Job failed";
        string body = $"Print job \"{jobName}\" has failed: {errorMessage}";

        await BroadcastJobNotificationAsync(
            NotificationType.JobFailed, subject, body, jobId, cancellationToken);
    }

    public async Task SendJobPausedAsync(
        string jobId,
        string jobName,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        string subject = "Job paused";
        string body = reason != null
            ? $"Print job \"{jobName}\" has been paused: {reason}"
            : $"Print job \"{jobName}\" has been paused.";

        await BroadcastJobNotificationAsync(
            NotificationType.JobPaused, subject, body, jobId, cancellationToken);
    }

    public async Task SendJobResumedAsync(
        string jobId,
        string jobName,
        CancellationToken cancellationToken = default)
    {
        string subject = "Job resumed";
        string body = $"Print job \"{jobName}\" has resumed printing.";

        await BroadcastJobNotificationAsync(
            NotificationType.JobResumed, subject, body, jobId, cancellationToken);
    }

    /// <summary>
    /// Broadcasts a job notification to all active users and sends a SignalR event.
    /// Since PrintJob does not track an owner, notifications are sent to all users.
    /// </summary>
    private async Task BroadcastJobNotificationAsync(
        NotificationType type,
        string subject,
        string body,
        string jobId,
        CancellationToken cancellationToken)
    {
        Guid? parsedJobId = Guid.TryParse(jobId, out Guid jid) ? jid : null;

        try
        {
            IReadOnlyList<UserDto> users = await usersRepository.GetUsersAsync(cancellationToken);
            IEnumerable<UserDto> activeUsers = users.Where(u => u.IsActive);

            foreach (UserDto user in activeUsers)
            {
                if (!await ShouldNotifyUserAsync(user.Id, type, cancellationToken))
                {
                    continue;
                }

                await SendNotificationAsync(user.Id, type, subject, body, parsedJobId, cancellationToken);
            }

            // Broadcast real-time event via SignalR so connected clients update immediately
            if (hubContext != null)
            {
                await hubContext.Clients.All.SendAsync(
                    "notificationreceived",
                    new { type = type.ToString(), subject, body, jobId = parsedJobId },
                    cancellationToken);
            }

            // Dispatch webhook for job events
            var webhookEventType = type switch
            {
                NotificationType.JobStarted => "job.started",
                NotificationType.JobCompleted => "job.completed",
                NotificationType.JobFailed => "job.failed",
                NotificationType.JobPaused => "job.paused",
                NotificationType.JobResumed => "job.resumed",
                _ => null
            };
            if (webhookEventType != null)
            {
                webhookService?.Enqueue(webhookEventType, new { jobId = parsedJobId, subject, body });
            }

            logger.LogInformation(
                "Job notification broadcast ({Type}) for job {JobId}: {Subject}",
                type, jobId, subject);
        }
        catch (Exception ex)
        {
            // Don't let notification failures break job processing
            logger.LogError(ex, "Error broadcasting {Type} notification for job {JobId}", type, jobId);
        }
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

    public async Task<NotificationPreferences?> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await dbContext.NotificationPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
    }

    public async Task UpdatePreferencesAsync(Guid userId, NotificationPreferences preferences, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.NotificationPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        if (existing is null)
        {
            preferences.Id = Guid.NewGuid().ToString();
            preferences.UserId = userId;
            preferences.CreatedAt = DateTime.UtcNow;
            preferences.UpdatedAt = DateTime.UtcNow;
            dbContext.NotificationPreferences.Add(preferences);
        }
        else
        {
            existing.EnableEmailNotifications = preferences.EnableEmailNotifications;
            existing.EnablePushNotifications = preferences.EnablePushNotifications;
            existing.EnableInAppNotifications = preferences.EnableInAppNotifications;
            existing.NotifyOnCompletion = preferences.NotifyOnCompletion;
            existing.NotifyOnFailure = preferences.NotifyOnFailure;
            existing.NotifyOnStart = preferences.NotifyOnStart;
            existing.NotifyOnPause = preferences.NotifyOnPause;
            existing.Frequency = preferences.Frequency;
            existing.RetentionDays = preferences.RetentionDays;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Updated notification preferences for user {UserId}", userId);
    }

    public async Task CleanupOldNotificationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Use the minimum retention across all user preferences, fallback to 30 days
            var allPrefs = await dbContext.NotificationPreferences.ToListAsync(cancellationToken);
            int retentionDays = allPrefs.Count > 0
                ? allPrefs.Min(p => p.RetentionDays)
                : 30;

            await notificationRepository.DeleteOldAsync(retentionDays, cancellationToken);
            logger.LogInformation("Notification cleanup completed (retention: {Days} days)", retentionDays);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during notification cleanup");
        }
    }

    /// <summary>
    /// Checks if a user should receive a notification of the given type based on their preferences.
    /// If no preferences are stored, the user receives all notifications (opt-out model).
    /// </summary>
    private async Task<bool> ShouldNotifyUserAsync(Guid userId, NotificationType type, CancellationToken cancellationToken)
    {
        var prefs = await dbContext.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        if (prefs is null)
        {
            return true; // no preferences set — default to all enabled
        }

        if (!prefs.EnableInAppNotifications)
        {
            return false;
        }

        return type switch
        {
            NotificationType.JobStarted => prefs.NotifyOnStart,
            NotificationType.JobCompleted => prefs.NotifyOnCompletion,
            NotificationType.JobFailed => prefs.NotifyOnFailure,
            NotificationType.JobPaused => prefs.NotifyOnPause,
            NotificationType.JobResumed => prefs.NotifyOnPause, // resume follows pause preference
            NotificationType.QueueAlert => true,                // always notify
            NotificationType.SystemAlert => true,               // always notify
            _ => true
        };
    }
}
