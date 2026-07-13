using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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
    /// Update user notification preferences.
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="preferences">The notification preferences to update.</param>
    /// <param name="preserveAttentionFields">
    /// When <c>true</c>, the 20 attention-row columns on the persisted entity are left
    /// untouched (issue #708 H2-v5). Legacy PUTs that carry only the four job rows set
    /// this so a concurrent newer-client attention update cannot be clobbered by a stale
    /// snapshot. When <c>false</c>, all 20 attention fields on <paramref name="preferences"/>
    /// are written through. Master flag columns (<c>EnablePushNotifications</c>, …) are
    /// derived from the final nine-row state either way and mirrored onto
    /// <paramref name="preferences"/> so callers building a response DTO see the actual
    /// persisted values.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task UpdatePreferencesAsync(Guid userId, NotificationPreferences preferences, bool preserveAttentionFields = false, CancellationToken cancellationToken = default);

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
    IWebPushNotificationSender? webPushNotificationSender = null,
    Func<string, CancellationToken, Task<bool>>? pushEndpointValidatorOverride = null,
    IEnumerable<INotificationChannel>? notificationChannels = null) : INotificationService
{
    private static readonly string[] KnownPushServiceHosts =
    {
        "fcm.googleapis.com",
        "updates.push.services.mozilla.com",
        "web.push.apple.com",
        "notify.windows.com",
    };

    private const int MaxPushSubscriptionsPerUser = 5;
    private const int ChannelFanOutConcurrency = 8;
    private const int PushFanOutConcurrency = 8;
    private readonly Func<string, CancellationToken, Task<bool>> pushEndpointValidator = pushEndpointValidatorOverride ?? IsValidPushEndpointAsync;
    private readonly IReadOnlyList<INotificationChannel> notificationChannels = notificationChannels?.ToList() ?? [];

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
            var pendingEmailTargets = new List<EmailDispatchTarget>();
            var pendingPushTargets = new List<PushDispatchTarget>();
            bool shouldDispatchTelegram = false;

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
                    pendingEmailTargets.Add(new EmailDispatchTarget(user));
                }

                if (ShouldDeliverToChannel(effectivePrefs, type, NotificationDeliveryChannel.Push))
                {
                    List<PushSubscription> userSubscriptions = await dbContext.PushSubscriptions
                        .AsNoTracking()
                        .Where(s => s.UserId == user.Id)
                        .ToListAsync(cancellationToken);
                    foreach (PushSubscription subscription in userSubscriptions)
                    {
                        pendingPushTargets.Add(new PushDispatchTarget(
                            subscription.Id,
                            user.Id,
                            subscription.Endpoint,
                            subscription.P256dh,
                            subscription.Auth));
                    }
                }

                shouldDispatchTelegram |= ShouldDeliverToChannel(effectivePrefs, type, NotificationDeliveryChannel.Telegram);
            }

            Guid? printerId = await ResolvePrinterIdForJobAsync(parsedJobId, cancellationToken);

            Task pushDispatchTask = Task.CompletedTask;
            if (pendingPushTargets.Count > 0)
            {
                string payload = JsonSerializer.Serialize(new
                {
                    type = type.ToString(),
                    title = subject,
                    subject,
                    body,
                    jobId = parsedJobId
                });
                pushDispatchTask = DispatchPushTargetsAsync(pendingPushTargets, payload, cancellationToken);
            }

            Task emailDispatchTask = Task.CompletedTask;
            if (pendingEmailTargets.Count > 0)
            {
                emailDispatchTask = DispatchEmailTargetsAsync(pendingEmailTargets, type, subject, body, cancellationToken);
            }

            Task telegramDispatchTask = Task.CompletedTask;
            if (shouldDispatchTelegram)
            {
                var message = new NotificationChannelMessage(type, subject, body, parsedJobId, printerId);
                telegramDispatchTask = DispatchNotificationChannelAsync(
                    NotificationDeliveryChannel.Telegram,
                    message,
                    cancellationToken);
            }

            await Task.WhenAll(pushDispatchTask, emailDispatchTask, telegramDispatchTask);

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

    public async Task UpdatePreferencesAsync(Guid userId, NotificationPreferences preferences, bool preserveAttentionFields = false, CancellationToken cancellationToken = default)
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

            // Derive master flags from the final nine-row state. For a new
            // row the caller-supplied `preferences` IS the final state, so we
            // derive directly onto it.
            ApplyMasterFlagsFromMatrix(preferences);
            dbContext.NotificationPreferences.Add(preferences);
        }
        else
        {
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
            existing.TelegramOnJobStarted = preferences.TelegramOnJobStarted;
            existing.TelegramOnJobCompleted = preferences.TelegramOnJobCompleted;
            existing.TelegramOnJobFailed = preferences.TelegramOnJobFailed;
            existing.TelegramOnJobPaused = preferences.TelegramOnJobPaused;
            existing.Frequency = preferences.Frequency;
            existing.RetentionDays = preferences.RetentionDays;

            // Issue #708 H2-v5: attention-row preservation lives here — the
            // single tracked read is authoritative. Legacy PUTs that omit
            // attention rows must set `preserveAttentionFields=true` so a
            // concurrent newer-client attention update cannot be overwritten
            // by a stale controller snapshot. Modern requests that address
            // attention rows leave the flag `false` and all 20 columns are
            // written through, preserving pre-fix modern-client semantics.
            if (!preserveAttentionFields)
            {
                existing.InAppOnPrinterFailure = preferences.InAppOnPrinterFailure;
                existing.EmailOnPrinterFailure = preferences.EmailOnPrinterFailure;
                existing.PushOnPrinterFailure = preferences.PushOnPrinterFailure;
                existing.TelegramOnPrinterFailure = preferences.TelegramOnPrinterFailure;
                existing.InAppOnFilamentRunout = preferences.InAppOnFilamentRunout;
                existing.EmailOnFilamentRunout = preferences.EmailOnFilamentRunout;
                existing.PushOnFilamentRunout = preferences.PushOnFilamentRunout;
                existing.TelegramOnFilamentRunout = preferences.TelegramOnFilamentRunout;
                existing.InAppOnHarvestReady = preferences.InAppOnHarvestReady;
                existing.EmailOnHarvestReady = preferences.EmailOnHarvestReady;
                existing.PushOnHarvestReady = preferences.PushOnHarvestReady;
                existing.TelegramOnHarvestReady = preferences.TelegramOnHarvestReady;
                existing.InAppOnMaintenanceDue = preferences.InAppOnMaintenanceDue;
                existing.EmailOnMaintenanceDue = preferences.EmailOnMaintenanceDue;
                existing.PushOnMaintenanceDue = preferences.PushOnMaintenanceDue;
                existing.TelegramOnMaintenanceDue = preferences.TelegramOnMaintenanceDue;
                existing.InAppOnPrinterOffline = preferences.InAppOnPrinterOffline;
                existing.EmailOnPrinterOffline = preferences.EmailOnPrinterOffline;
                existing.PushOnPrinterOffline = preferences.PushOnPrinterOffline;
                existing.TelegramOnPrinterOffline = preferences.TelegramOnPrinterOffline;
            }

            // Issue #708 H1-v5: master flags are derived data — the OR of the
            // final nine event×channel rows on the tracked entity, computed
            // AFTER attention-row preservation. Deriving here (rather than in
            // the controller) is the single source of truth so a legacy PUT
            // whose caller doesn't know the persisted attention state still
            // ends up with an accurate master flag.
            ApplyMasterFlagsFromMatrix(existing);
            existing.UpdatedAt = DateTime.UtcNow;

            // Mirror the final persisted state back onto the caller's DTO so
            // the controller's response body reflects reality (all 20 attention
            // fields + the four master flags) instead of the pre-service
            // snapshot the controller assembled from the request.
            MirrorAttentionAndMasterFlags(existing, preferences);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Updated notification preferences for user {UserId}", userId);
    }

    private static void ApplyMasterFlagsFromMatrix(NotificationPreferences prefs)
    {
        prefs.EnableInAppNotifications =
            prefs.InAppOnJobStarted
            || prefs.InAppOnJobCompleted
            || prefs.InAppOnJobFailed
            || prefs.InAppOnJobPaused
            || prefs.InAppOnPrinterFailure
            || prefs.InAppOnFilamentRunout
            || prefs.InAppOnHarvestReady
            || prefs.InAppOnMaintenanceDue
            || prefs.InAppOnPrinterOffline;
        prefs.EnableEmailNotifications =
            prefs.EmailOnJobStarted
            || prefs.EmailOnJobCompleted
            || prefs.EmailOnJobFailed
            || prefs.EmailOnJobPaused
            || prefs.EmailOnPrinterFailure
            || prefs.EmailOnFilamentRunout
            || prefs.EmailOnHarvestReady
            || prefs.EmailOnMaintenanceDue
            || prefs.EmailOnPrinterOffline;
        prefs.EnablePushNotifications =
            prefs.PushOnJobStarted
            || prefs.PushOnJobCompleted
            || prefs.PushOnJobFailed
            || prefs.PushOnJobPaused
            || prefs.PushOnPrinterFailure
            || prefs.PushOnFilamentRunout
            || prefs.PushOnHarvestReady
            || prefs.PushOnMaintenanceDue
            || prefs.PushOnPrinterOffline;
        prefs.EnableTelegramNotifications =
            prefs.TelegramOnJobStarted
            || prefs.TelegramOnJobCompleted
            || prefs.TelegramOnJobFailed
            || prefs.TelegramOnJobPaused
            || prefs.TelegramOnPrinterFailure
            || prefs.TelegramOnFilamentRunout
            || prefs.TelegramOnHarvestReady
            || prefs.TelegramOnMaintenanceDue
            || prefs.TelegramOnPrinterOffline;
    }

    private static void MirrorAttentionAndMasterFlags(NotificationPreferences source, NotificationPreferences target)
    {
        target.EnableInAppNotifications = source.EnableInAppNotifications;
        target.EnableEmailNotifications = source.EnableEmailNotifications;
        target.EnablePushNotifications = source.EnablePushNotifications;
        target.EnableTelegramNotifications = source.EnableTelegramNotifications;
        target.InAppOnPrinterFailure = source.InAppOnPrinterFailure;
        target.EmailOnPrinterFailure = source.EmailOnPrinterFailure;
        target.PushOnPrinterFailure = source.PushOnPrinterFailure;
        target.TelegramOnPrinterFailure = source.TelegramOnPrinterFailure;
        target.InAppOnFilamentRunout = source.InAppOnFilamentRunout;
        target.EmailOnFilamentRunout = source.EmailOnFilamentRunout;
        target.PushOnFilamentRunout = source.PushOnFilamentRunout;
        target.TelegramOnFilamentRunout = source.TelegramOnFilamentRunout;
        target.InAppOnHarvestReady = source.InAppOnHarvestReady;
        target.EmailOnHarvestReady = source.EmailOnHarvestReady;
        target.PushOnHarvestReady = source.PushOnHarvestReady;
        target.TelegramOnHarvestReady = source.TelegramOnHarvestReady;
        target.InAppOnMaintenanceDue = source.InAppOnMaintenanceDue;
        target.EmailOnMaintenanceDue = source.EmailOnMaintenanceDue;
        target.PushOnMaintenanceDue = source.PushOnMaintenanceDue;
        target.TelegramOnMaintenanceDue = source.TelegramOnMaintenanceDue;
        target.InAppOnPrinterOffline = source.InAppOnPrinterOffline;
        target.EmailOnPrinterOffline = source.EmailOnPrinterOffline;
        target.PushOnPrinterOffline = source.PushOnPrinterOffline;
        target.TelegramOnPrinterOffline = source.TelegramOnPrinterOffline;
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
            EnableTelegramNotifications = false,
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
            TelegramOnJobStarted = false,
            TelegramOnJobCompleted = false,
            TelegramOnJobFailed = false,
            TelegramOnJobPaused = false,
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
            NotificationDeliveryChannel.Telegram => preferences.EnableTelegramNotifications,
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

    private async Task DispatchPushTargetsAsync(
        IReadOnlyList<PushDispatchTarget> targets,
        string payload,
        CancellationToken cancellationToken)
    {
        if (webPushNotificationSender is null || targets.Count == 0)
        {
            return;
        }

        var outcomes = new ConcurrentBag<PushDispatchOutcome>();
        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = PushFanOutConcurrency,
                CancellationToken = cancellationToken
            },
            async (target, ct) =>
            {
                try
                {
                    if (!await pushEndpointValidator(target.Endpoint, ct))
                    {
                        outcomes.Add(new PushDispatchOutcome(target.SubscriptionId, target.UserId, target.Endpoint, Success: false, Expired: false, Error: "Endpoint failed local validation"));
                        return;
                    }

                    var dispatchSubscription = new PushSubscription
                    {
                        Id = target.SubscriptionId,
                        UserId = target.UserId,
                        Endpoint = target.Endpoint,
                        P256dh = target.P256dh,
                        Auth = target.Auth
                    };

                    WebPushDispatchResult result = await webPushNotificationSender.SendAsync(dispatchSubscription, payload, ct);
                    outcomes.Add(new PushDispatchOutcome(target.SubscriptionId, target.UserId, target.Endpoint, result.Success, result.SubscriptionExpired, result.Error));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Error dispatching web push notification for user {UserId} endpoint {Endpoint}",
                        target.UserId,
                        target.Endpoint);
                }
            });

        var outcomeList = outcomes.ToList();
        var expiredIds = outcomeList
            .Where(o => o.Expired)
            .Select(o => o.SubscriptionId)
            .Distinct()
            .ToList();
        var successfulIds = outcomeList
            .Where(o => o.Success && !o.Expired)
            .Select(o => o.SubscriptionId)
            .Distinct()
            .ToList();

        foreach (PushDispatchOutcome failed in outcomeList.Where(o => !o.Success && !o.Expired))
        {
            logger.LogWarning(
                "Web push delivery failed for user {UserId} endpoint {Endpoint}: {Error}",
                failed.UserId,
                failed.Endpoint,
                failed.Error);
        }

        bool hasUpdates = false;
        if (successfulIds.Count > 0)
        {
            DateTime now = DateTime.UtcNow;
            List<PushSubscription> successfulSubscriptions = await dbContext.PushSubscriptions
                .Where(s => successfulIds.Contains(s.Id))
                .ToListAsync(cancellationToken);
            foreach (PushSubscription subscription in successfulSubscriptions)
            {
                subscription.LastUsedAt = now;
            }

            hasUpdates = successfulSubscriptions.Count > 0;
        }

        if (expiredIds.Count > 0)
        {
            List<PushSubscription> expiredSubscriptions = await dbContext.PushSubscriptions
                .Where(s => expiredIds.Contains(s.Id))
                .ToListAsync(cancellationToken);
            if (expiredSubscriptions.Count > 0)
            {
                dbContext.PushSubscriptions.RemoveRange(expiredSubscriptions);
                hasUpdates = true;
            }
        }

        if (hasUpdates)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task DispatchEmailTargetsAsync(
        IReadOnlyList<EmailDispatchTarget> targets,
        NotificationType type,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return;
        }

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ChannelFanOutConcurrency,
                CancellationToken = cancellationToken
            },
            async (target, ct) =>
            {
                try
                {
                    await SendEmailNotificationAsync(target.User, type, subject, body, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Error dispatching email notification for user {UserId} target {Email}",
                        target.User.Id,
                        target.User.Email);
                }
            });
    }

    private async Task DispatchNotificationChannelAsync(
        NotificationDeliveryChannel channel,
        NotificationChannelMessage message,
        CancellationToken cancellationToken)
    {
        foreach (INotificationChannel notificationChannel in notificationChannels.Where(c => c.Channel == channel))
        {
            try
            {
                NotificationChannelDispatchResult result = await notificationChannel.SendAsync(message, cancellationToken);
                if (!result.Success)
                {
                    logger.LogWarning(
                        "Notification channel {Channel} delivery failed for {Type}: {Error}",
                        channel,
                        message.Type,
                        result.Error);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Error dispatching notification channel {Channel} for {Type}",
                    channel,
                    message.Type);
            }
        }
    }

    private async Task<Guid?> ResolvePrinterIdForJobAsync(Guid? jobId, CancellationToken cancellationToken)
    {
        if (!jobId.HasValue)
        {
            return null;
        }

        return await dbContext.PrintJobs
            .AsNoTracking()
            .Where(job => job.Id == jobId.Value)
            .Select(job => job.AssignedPrinterId ?? job.SourcePrinterId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SavePushSubscriptionAsync(Guid userId, string endpoint, string p256dh, string auth, CancellationToken cancellationToken = default)
    {
        if (!await pushEndpointValidator(endpoint, cancellationToken))
        {
            throw new ArgumentException("Endpoint must be an absolute HTTPS URL and cannot target local/private hosts", nameof(endpoint));
        }

        if (!IsValidPushKeys(p256dh, auth))
        {
            throw new ArgumentException("Subscription keys p256dh/auth are invalid");
        }

        if (dbContext.Database.IsRelational())
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
            await UpsertPushSubscriptionAsync(userId, endpoint, p256dh, auth, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await UpsertPushSubscriptionAsync(userId, endpoint, p256dh, auth, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

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

    private static async Task<bool> IsValidPushEndpointAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = uri.IdnHost.Trim('[', ']');
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsKnownPushServiceHost(host))
        {
            return false;
        }

        try
        {
            IPAddress[] resolved = await Dns.GetHostAddressesAsync(host, cancellationToken);
            return resolved.Length > 0 && resolved.All(IsPublicRoutableAddress);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsPublicRoutableAddress(IPAddress address)
    {
        IPAddress ipAddress = address;
        if (ipAddress.IsIPv4MappedToIPv6)
        {
            ipAddress = ipAddress.MapToIPv4();
        }
        else if (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            byte[] v6Bytes = ipAddress.GetAddressBytes();
            bool isIpv4Compatible = v6Bytes.Take(12).All(b => b == 0);
            if (isIpv4Compatible)
            {
                ipAddress = new IPAddress(v6Bytes.Skip(12).ToArray());
            }
        }

        if (IPAddress.IsLoopback(ipAddress))
        {
            return false;
        }

        if (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ipAddress.Equals(IPAddress.IPv6None) || ipAddress.Equals(IPAddress.IPv6Any))
            {
                return false;
            }

            if (ipAddress.IsIPv6LinkLocal || ipAddress.IsIPv6Multicast || ipAddress.IsIPv6SiteLocal || ipAddress.IsIPv6Teredo)
            {
                return false;
            }

            byte[] v6 = ipAddress.GetAddressBytes();
            bool isNat64WellKnownPrefix = v6[0] == 0x00
                && v6[1] == 0x64
                && v6[2] == 0xFF
                && v6[3] == 0x9B
                && v6[4] == 0x00
                && v6[5] == 0x00
                && v6[6] == 0x00
                && v6[7] == 0x00
                && v6[8] == 0x00
                && v6[9] == 0x00
                && v6[10] == 0x00
                && v6[11] == 0x00;
            if (isNat64WellKnownPrefix)
            {
                return false;
            }

            return (v6[0] & 0xFE) != 0xFC;
        }

        byte[] ipv4Bytes = ipAddress.GetAddressBytes();
        if (ipv4Bytes[0] == 0 || ipv4Bytes[0] == 10 || ipv4Bytes[0] == 127)
        {
            return false;
        }

        if (ipv4Bytes[0] == 100 && ipv4Bytes[1] >= 64 && ipv4Bytes[1] <= 127)
        {
            return false;
        }

        if (ipv4Bytes[0] == 169 && ipv4Bytes[1] == 254)
        {
            return false;
        }

        if (ipv4Bytes[0] == 172 && ipv4Bytes[1] >= 16 && ipv4Bytes[1] <= 31)
        {
            return false;
        }

        if (ipv4Bytes[0] == 192 && ipv4Bytes[1] == 168)
        {
            return false;
        }

        if (ipv4Bytes[0] == 198 && (ipv4Bytes[1] == 18 || ipv4Bytes[1] == 19))
        {
            return false;
        }

        if (ipv4Bytes[0] >= 224)
        {
            return false;
        }

        return true;
    }

    private static bool IsKnownPushServiceHost(string host)
    {
        foreach (string knownHost in KnownPushServiceHosts)
        {
            if (string.Equals(host, knownHost, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith($".{knownHost}", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsValidPushKeys(string p256dh, string auth)
    {
        if (string.IsNullOrWhiteSpace(p256dh) || string.IsNullOrWhiteSpace(auth))
        {
            return false;
        }

        if (p256dh.Length > 512 || auth.Length > 256)
        {
            return false;
        }

        return TryDecodeBase64Url(p256dh, out byte[]? p256dhBytes)
            && p256dhBytes.Length is >= 32 and <= 200
            && TryDecodeBase64Url(auth, out byte[]? authBytes)
            && authBytes.Length is >= 8 and <= 64;
    }

    private static bool TryDecodeBase64Url(string input, out byte[] decoded)
    {
        decoded = Array.Empty<byte>();
        try
        {
            foreach (char ch in input)
            {
                if (char.IsWhiteSpace(ch))
                {
                    return false;
                }

                if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '='))
                {
                    return false;
                }
            }

            string normalized = input.Replace('-', '+').Replace('_', '/');
            int firstPadding = normalized.IndexOf('=');
            if (firstPadding >= 0 && normalized[firstPadding..].Any(c => c != '='))
            {
                return false;
            }

            normalized = normalized.TrimEnd('=');
            int padding = normalized.Length % 4;
            if (padding > 0)
            {
                normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
            }

            decoded = Convert.FromBase64String(normalized);
            return decoded.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task UpsertPushSubscriptionAsync(Guid userId, string endpoint, string p256dh, string auth, CancellationToken cancellationToken)
    {
        PushSubscription? existing = await dbContext.PushSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Endpoint == endpoint, cancellationToken);

        if (existing is not null)
        {
            existing.P256dh = p256dh;
            existing.Auth = auth;
            existing.LastUsedAt = DateTime.UtcNow;
            return;
        }

        int existingCount = await dbContext.PushSubscriptions.CountAsync(s => s.UserId == userId, cancellationToken);
        if (existingCount >= MaxPushSubscriptionsPerUser)
        {
            throw new ArgumentException($"Maximum of {MaxPushSubscriptionsPerUser} push subscriptions per user exceeded");
        }

        dbContext.PushSubscriptions.Add(new PushSubscription
        {
            UserId = userId,
            Endpoint = endpoint,
            P256dh = p256dh,
            Auth = auth,
            CreatedAt = DateTime.UtcNow
        });
    }

    private sealed record EmailDispatchTarget(UserDto User);

    private sealed record PushDispatchTarget(string SubscriptionId, Guid UserId, string Endpoint, string P256dh, string Auth);

    private sealed record PushDispatchOutcome(string SubscriptionId, Guid UserId, string Endpoint, bool Success, bool Expired, string? Error);
}
