using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Email;
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

    /// <summary>
    /// Save a push subscription for a user (upsert by endpoint).
    /// </summary>
    Task SavePushSubscriptionAsync(Guid userId, string endpoint, string p256dh, string auth, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a specific push subscription for a user, identified by endpoint URL.
    /// </summary>
    Task DeletePushSubscriptionAsync(Guid userId, string endpoint, CancellationToken cancellationToken = default);
}

public class NotificationService(
    INotificationRepository notificationRepository,
    IUsersRepository usersRepository,
    ILogger<NotificationService> logger,
    AppDbContext dbContext,
    IHubContext<PrinterHub>? hubContext = null,
    IWebhookService? webhookService = null,
    IEmailService? emailService = null,
    IWebPushNotificationSender? webPushNotificationSender = null) : INotificationService
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
            // Broadcast realtime/webhook first so UI and integrations are not delayed by channel fan-out work.
            if (hubContext != null)
            {
                await hubContext.Clients.All.SendAsync(
                    "notificationreceived",
                    new { type = type.ToString(), subject, body, jobId = parsedJobId },
                    cancellationToken);
            }

            string? webhookEventType = type switch
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

            IReadOnlyList<UserDto> users = await usersRepository.GetUsersAsync(cancellationToken);
            IEnumerable<UserDto> activeUsers = users.Where(u => u.IsActive);

            foreach (UserDto user in activeUsers)
            {
                NotificationPreferences? prefs = await GetPreferencesAsync(user.Id, cancellationToken);
                NotificationPreferences effectivePrefs = prefs ?? BuildDefaultPreferences(user.Id);

                if (ShouldDeliverToChannel(effectivePrefs, type, NotificationDeliveryChannel.InApp))
                {
                    await SendNotificationAsync(user.Id, type, subject, body, parsedJobId, cancellationToken);
                }

                if (ShouldDeliverToChannel(effectivePrefs, type, NotificationDeliveryChannel.Email))
                {
                    await SendEmailNotificationAsync(user, type, subject, body, cancellationToken);
                }

                if (ShouldDeliverToChannel(effectivePrefs, type, NotificationDeliveryChannel.Push))
                {
                    await SendPushNotificationsAsync(user.Id, type, subject, body, parsedJobId, cancellationToken);
                }
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
            .AsNoTracking()
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
            preferences.InAppOnJobFailed = true;
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
            existing.InAppOnJobStarted = preferences.InAppOnJobStarted;
            existing.InAppOnJobCompleted = preferences.InAppOnJobCompleted;
            existing.InAppOnJobFailed = true;
            existing.InAppOnJobPaused = preferences.InAppOnJobPaused;
            existing.EmailOnJobStarted = preferences.EmailOnJobStarted;
            existing.EmailOnJobCompleted = preferences.EmailOnJobCompleted;
            existing.EmailOnJobFailed = preferences.EmailOnJobFailed;
            existing.EmailOnJobPaused = preferences.EmailOnJobPaused;
            existing.PushOnJobStarted = preferences.PushOnJobStarted;
            existing.PushOnJobCompleted = preferences.PushOnJobCompleted;
            existing.PushOnJobFailed = preferences.PushOnJobFailed;
            existing.PushOnJobPaused = preferences.PushOnJobPaused;
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

    private static NotificationPreferences BuildDefaultPreferences(Guid userId)
    {
        return new NotificationPreferences
        {
            UserId = userId,
            EnableEmailNotifications = true,
            EnablePushNotifications = true,
            EnableInAppNotifications = true,
            NotifyOnCompletion = true,
            NotifyOnFailure = true,
            NotifyOnStart = false,
            NotifyOnPause = true,
            InAppOnJobStarted = false,
            InAppOnJobCompleted = true,
            InAppOnJobFailed = true,
            InAppOnJobPaused = true,
            EmailOnJobStarted = false,
            EmailOnJobCompleted = true,
            EmailOnJobFailed = true,
            EmailOnJobPaused = true,
            PushOnJobStarted = false,
            PushOnJobCompleted = true,
            PushOnJobFailed = true,
            PushOnJobPaused = true,
            Frequency = NotificationFrequency.RealTime,
            RetentionDays = 30
        };
    }

    private static bool ShouldDeliverToChannel(NotificationPreferences preferences, NotificationType type, NotificationDeliveryChannel channel)
    {
        if (type == NotificationType.JobFailed && channel == NotificationDeliveryChannel.InApp)
        {
            return true;
        }

        bool channelGloballyEnabled = channel switch
        {
            NotificationDeliveryChannel.InApp => preferences.EnableInAppNotifications,
            NotificationDeliveryChannel.Email => preferences.EnableEmailNotifications,
            NotificationDeliveryChannel.Push => preferences.EnablePushNotifications,
            _ => false
        };

        if (!channelGloballyEnabled)
        {
            return false;
        }

        if (!IsPrintEventType(type))
        {
            return channel == NotificationDeliveryChannel.InApp;
        }

        return preferences.IsChannelEnabled(type, channel);
    }

    private static bool IsPrintEventType(NotificationType type)
    {
        return type is NotificationType.JobStarted
            or NotificationType.JobCompleted
            or NotificationType.JobFailed
            or NotificationType.JobPaused
            or NotificationType.JobResumed;
    }

    private async Task SendEmailNotificationAsync(UserDto user, NotificationType type, string subject, string body, CancellationToken cancellationToken)
    {
        if (emailService is null || string.IsNullOrWhiteSpace(user.Email))
        {
            return;
        }

        var metadata = new Dictionary<string, string>
        {
            ["notificationType"] = type.ToString()
        };

        string encodedBody = WebUtility.HtmlEncode(body);
        var message = new EmailMessage(user.Email, subject, PlainBody: body, HtmlBody: $"<p>{encodedBody}</p>", TemplateKey: "PrintEvent", Metadata: metadata);
        EmailDispatchResult result = await emailService.SendAsync(message, cancellationToken);
        if (!result.Success)
        {
            logger.LogWarning("Email delivery failed for user {UserId}: {Error}", user.Id, result.Error ?? result.ProviderMessage);
        }
    }

    private async Task SendPushNotificationsAsync(Guid userId, NotificationType type, string subject, string body, Guid? jobId, CancellationToken cancellationToken)
    {
        if (webPushNotificationSender is null)
        {
            return;
        }

        List<PushSubscription> subscriptions = await dbContext.PushSubscriptions
            .Where(s => s.UserId == userId)
            .ToListAsync(cancellationToken);

        if (subscriptions.Count == 0)
        {
            return;
        }

        string payload = JsonSerializer.Serialize(new
        {
            type = type.ToString(),
            subject,
            body,
            jobId
        });

        var expiredSubscriptions = new List<PushSubscription>();
        bool hasSubscriptionUpdates = false;

        foreach (PushSubscription subscription in subscriptions)
        {
            WebPushDispatchResult result = await webPushNotificationSender.SendAsync(subscription, payload, cancellationToken);
            if (result.SubscriptionExpired)
            {
                expiredSubscriptions.Add(subscription);
            }
            else if (!result.Success)
            {
                logger.LogWarning("Web push delivery failed for user {UserId} endpoint {Endpoint}: {Error}", userId, subscription.Endpoint, result.Error);
            }
            else
            {
                subscription.LastUsedAt = DateTime.UtcNow;
                hasSubscriptionUpdates = true;
            }
        }

        bool hasUpdates = hasSubscriptionUpdates;
        if (expiredSubscriptions.Count > 0)
        {
            dbContext.PushSubscriptions.RemoveRange(expiredSubscriptions);
            hasUpdates = true;
        }

        if (hasUpdates)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task SavePushSubscriptionAsync(Guid userId, string endpoint, string p256dh, string auth, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.PushSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Endpoint == endpoint, cancellationToken);

        if (existing is not null)
        {
            existing.P256dh = p256dh;
            existing.Auth = auth;
            existing.LastUsedAt = DateTime.UtcNow;
        }
        else
        {
            dbContext.PushSubscriptions.Add(new PushSubscription
            {
                UserId = userId,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
                CreatedAt = DateTime.UtcNow
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Saved push subscription for user {UserId}", userId);
    }

    public async Task DeletePushSubscriptionAsync(Guid userId, string endpoint, CancellationToken cancellationToken = default)
    {
        var subscription = await dbContext.PushSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Endpoint == endpoint, cancellationToken);

        if (subscription is not null)
        {
            dbContext.PushSubscriptions.Remove(subscription);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Deleted push subscription for user {UserId} endpoint {Endpoint}", userId, endpoint);
        }
    }
}
