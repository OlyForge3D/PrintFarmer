using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Email;
using Farm.Infrastructure.Services.Notifications.NativePush;
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
    /// are written through. Global channel controls (<c>EnablePushNotifications</c>, …)
    /// are copied independently and never derived from row values. The final persisted
    /// state is mirrored onto <paramref name="preferences"/> for response consistency.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task UpdatePreferencesAsync(Guid userId, NotificationPreferences preferences, bool preserveAttentionFields = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Authoritative preference-patch update. Applies <paramref name="patch"/> as a
    /// diff over the persisted preferences row: supplied matrix rows overwrite the
    /// corresponding four columns on the tracked entity, every omitted job/attention
    /// row is preserved, and each global <c>Enable{Channel}Notifications</c> control
    /// changes only when explicitly supplied. The read/mutate/save runs under a
    /// serializable transaction on relational providers so concurrent partial writes
    /// merge from a fresh authoritative read. Returns the persisted entity so controllers
    /// can build a response DTO
    /// that matches the row on disk rather than the caller's transient request view.
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="patch">The preference patch to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<NotificationPreferences> UpdatePreferencesAsync(Guid userId, NotificationPreferencesUpdate patch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hicks #6: authoritative attention-category preference upsert. Under a
    /// serializable transaction with fresh-context bounded retry the service
    /// reads the persisted map, merges <paramref name="updates"/> using
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>, enforces cumulative
    /// cardinality / UTF-8 byte bounds, and saves atomically. Concurrent
    /// first-creates converge on a single row; concurrent disjoint-key updates
    /// both persist. Rejection cases (bounds exceeded) return the typed
    /// <see cref="AttentionCategoryUpdateResult"/> without touching the row.
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="updates">Category-key → opt-in map to merge. May be empty.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<AttentionCategoryUpdateResult> UpdateAttentionCategoryPreferencesAsync(
        Guid userId,
        IReadOnlyDictionary<string, bool> updates,
        CancellationToken cancellationToken = default);

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
    IEnumerable<INotificationChannel>? notificationChannels = null,
    IDbContextFactory<AppDbContext>? preferencesContextFactory = null) : INotificationService
{
    // Hicks #2: retries use a FRESH DbContext per attempt via the factory so
    // stale change-tracker snapshots from a losing serializable transaction
    // never leak into the retried attempt. When absent (tests wire a bespoke
    // in-memory context directly), the retry helper falls back to a single
    // attempt on the injected context. Production wiring always supplies the
    // factory via DI.
    private readonly IDbContextFactory<AppDbContext>? _preferencesContextFactory = preferencesContextFactory;

    /// <summary>
    /// Bishop #12 / Hicks #3 deterministic test seam. Fires ONCE per
    /// preference-patch attempt, AFTER the tracked row has been read but
    /// BEFORE mutations are applied and <see cref="AppDbContext.SaveChangesAsync"/>
    /// is invoked. Production default is <see langword="null"/> — no-op — and
    /// this property is not surfaced through <see cref="INotificationService"/>.
    /// Race tests use it to inject a barrier that forces one writer's save to
    /// race a concurrent writer's commit, driving the serializable / retry
    /// path deterministically instead of relying on OS-level scheduling.
    /// </summary>
    internal Func<AppDbContext, CancellationToken, Task>? OnAfterPreferenceReadForTestsAsync { get; set; }

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
                await hubContext.Clients.Group(AuthorizedHubGroups.Farm).SendAsync(
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
                type, LogSanitizer.Sanitize(jobId), LogSanitizer.Sanitize(subject));
        }
        catch (Exception ex)
        {
            // Don't let notification failures break job processing
            logger.LogError(ex, "Error broadcasting {Type} notification for job {JobId}", type, LogSanitizer.Sanitize(jobId));
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
            logger.LogInformation("Notification sent to user {UserId}: {Subject}", userId, LogSanitizer.Sanitize(subject));
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
        logger.LogInformation("Notification {NotificationId} marked as read", LogSanitizer.Sanitize(notificationId));
    }

    public async Task MarkMultipleAsReadAsync(IEnumerable<string> notificationIds, CancellationToken cancellationToken = default)
    {
        await notificationRepository.MarkMultipleAsReadAsync(notificationIds, cancellationToken);
        logger.LogInformation("Marked multiple notifications as read");
    }

    public async Task DeleteAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        await notificationRepository.DeleteAsync(notificationId, cancellationToken);
        logger.LogInformation("Notification {NotificationId} deleted", LogSanitizer.Sanitize(notificationId));
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
        // Hicks #2 hardening: the entire read/mutate/derive/save unit runs under a
        // serializable transaction on relational providers so a concurrent legacy
        // PUT cannot land between this request's read and save. Provider
        // serialization / deadlock / unique-index conflicts are shed via a
        // bounded whole-operation retry over a FRESH DbContext per attempt so
        // the change-tracker never carries losing-attempt snapshots forward.
        // Non-relational providers (InMemory used only in unit tests) fall
        // through without a transaction and without retry — no cross-context
        // race can arise there.
        //
        // Explicitly NOT retried: any exception thrown by
        // <see cref="UpdatePreferencesCoreAsync"/> that isn't a recognised
        // transient. A malformed patch surfaces on the first attempt.
        if (dbContext.Database.IsRelational() && _preferencesContextFactory is not null)
        {
            await PreferenceConcurrencyRetry.ExecuteAsync<int>(
                _preferencesContextFactory,
                dbContext,
                async (freshContext, ct) =>
                {
                    await using var transaction = await BeginPreferenceTransactionAsync(freshContext, ct);
                    await UpdatePreferencesCoreOnContextAsync(freshContext, userId, preferences, preserveAttentionFields, ct);
                    await transaction.CommitAsync(ct);
                    return 0;
                },
                logger,
                cancellationToken);
            return;
        }

        if (dbContext.Database.IsRelational())
        {
            await using var transaction = await BeginPreferenceTransactionAsync(dbContext, cancellationToken);
            await UpdatePreferencesCoreAsync(userId, preferences, preserveAttentionFields, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await UpdatePreferencesCoreAsync(userId, preferences, preserveAttentionFields, cancellationToken);
    }

    private async Task UpdatePreferencesCoreAsync(Guid userId, NotificationPreferences preferences, bool preserveAttentionFields, CancellationToken cancellationToken)
    {
        await UpdatePreferencesCoreOnContextAsync(dbContext, userId, preferences, preserveAttentionFields, cancellationToken);
    }

    private async Task UpdatePreferencesCoreOnContextAsync(AppDbContext ctx, Guid userId, NotificationPreferences preferences, bool preserveAttentionFields, CancellationToken cancellationToken)
    {
        var existing = await ctx.NotificationPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        if (existing is null)
        {
            preferences.Id = Guid.NewGuid().ToString();
            preferences.UserId = userId;
            preferences.InAppOnJobFailed = true;
            preferences.CreatedAt = DateTime.UtcNow;
            preferences.UpdatedAt = DateTime.UtcNow;

            // Global channel controls are independent kill switches. The caller's
            // complete entity already carries their authoritative values; matrix
            // rows must never synthesize an opt-in.
            ctx.NotificationPreferences.Add(preferences);
        }
        else
        {
            existing.EnableInAppNotifications = preferences.EnableInAppNotifications;
            existing.EnableEmailNotifications = preferences.EnableEmailNotifications;
            existing.EnablePushNotifications = preferences.EnablePushNotifications;
            existing.EnableTelegramNotifications = preferences.EnableTelegramNotifications;
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

            // The four global controls are independent kill switches copied above.
            // Never derive them from row values: doing so can silently reopen a
            // channel that an operator explicitly disabled.
            existing.UpdatedAt = DateTime.UtcNow;

            // Mirror the final persisted state back onto the caller's DTO so
            // the controller's response body reflects reality (all 20 attention
            // fields + the four master flags) instead of the pre-service
            // snapshot the controller assembled from the request.
            MirrorAttentionAndMasterFlags(existing, preferences);
        }

        await ctx.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Updated notification preferences for user {UserId}", userId);
    }

    public async Task<NotificationPreferences> UpdatePreferencesAsync(Guid userId, NotificationPreferencesUpdate patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);

        // Vasquez v6 B3 + Bishop v6 master-flag stale-order race + Hicks #2:
        // the entire read/mutate/save sequence is one atomic step. Under a serializable transaction plus a bounded
        // whole-operation retry over a fresh DbContext, a concurrent legacy
        // PUT that loses the serialization contest is retried until it
        // observes the winner's state (or the retry budget is exhausted, at
        // which point the transient surfaces to the caller). Only recognised
        // transient signals trigger retry; validation errors and cancellation
        // propagate immediately.
        //
        // Non-relational providers (InMemory in unit tests) fall through
        // without transaction and without retry — mutation is single-context
        // and no cross-context race exists.
        if (dbContext.Database.IsRelational() && _preferencesContextFactory is not null)
        {
            return await PreferenceConcurrencyRetry.ExecuteAsync(
                _preferencesContextFactory,
                dbContext,
                async (freshContext, ct) =>
                {
                    await using var transaction = await BeginPreferenceTransactionAsync(freshContext, ct);
                    NotificationPreferences persistedInner = await ApplyPatchOnContextAsync(freshContext, userId, patch, ct);
                    await transaction.CommitAsync(ct);
                    return persistedInner;
                },
                logger,
                cancellationToken);
        }

        if (dbContext.Database.IsRelational())
        {
            await using var transaction = await BeginPreferenceTransactionAsync(dbContext, cancellationToken);
            var persisted = await ApplyPatchAsync(userId, patch, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return persisted;
        }

        return await ApplyPatchAsync(userId, patch, cancellationToken);
    }

    private async Task<NotificationPreferences> ApplyPatchAsync(Guid userId, NotificationPreferencesUpdate patch, CancellationToken cancellationToken)
    {
        return await ApplyPatchOnContextAsync(dbContext, userId, patch, cancellationToken);
    }

    private async Task<NotificationPreferences> ApplyPatchOnContextAsync(AppDbContext ctx, Guid userId, NotificationPreferencesUpdate patch, CancellationToken cancellationToken)
    {
        var tracked = await ctx.NotificationPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        // Bishop #12 / Hicks #3: deterministic race barrier. In production the
        // hook is null and this call is a JIT-nop; in tests the injected
        // delegate lets the fixture pause this attempt after the read so a
        // concurrent writer's commit lands first, driving the retry path.
        if (OnAfterPreferenceReadForTestsAsync is { } hook)
        {
            await hook(ctx, cancellationToken);
        }

        bool isNew = tracked is null;
        if (tracked is null)
        {
            // Hicks #3: new-user persistence MUST use the same canonical
            // defaults the GET returns for a user with no persisted row, so a
            // first partial modern PUT never mutates omitted rows. Delegating
            // to the shared helper keeps both paths in sync.
            tracked = NotificationPreferencesDefaults.Create(userId);
            tracked.Id = Guid.NewGuid().ToString();
            tracked.CreatedAt = DateTime.UtcNow;
            ctx.NotificationPreferences.Add(tracked);
        }

        // Scalars only apply when present in the patch (Hicks #5 — a bare
        // `{}` PUT must not clobber persisted Frequency / RetentionDays with
        // model-binder defaults). Every nullable field carries the same
        // "null = omitted, preserve" semantics throughout the legacy branch.
        if (patch.Frequency.HasValue)
        {
            tracked.Frequency = patch.Frequency.Value;
        }

        if (patch.RetentionDays.HasValue)
        {
            tracked.RetentionDays = patch.RetentionDays.Value;
        }

        // Global channel controls are independent user kill switches. A supplied
        // value is persisted verbatim; an omitted value is a strict no-op. They
        // are intentionally not derived from matrix rows below.
        if (patch.EnableInAppNotifications is bool enableInAppGlobal)
        {
            tracked.EnableInAppNotifications = enableInAppGlobal;
        }

        if (patch.EnableEmailNotifications is bool enableEmailGlobal)
        {
            tracked.EnableEmailNotifications = enableEmailGlobal;
        }

        if (patch.EnablePushNotifications is bool enablePushGlobal)
        {
            tracked.EnablePushNotifications = enablePushGlobal;
        }

        if (patch.EnableTelegramNotifications is bool enableTelegramGlobal)
        {
            tracked.EnableTelegramNotifications = enableTelegramGlobal;
        }

        if (patch.MatrixRows is null)
        {
            // Legacy branch. Only the four job rows are addressed, derived
            // from the top-level per-event toggles crossed with the four
            // top-level per-channel toggles. Attention rows on the tracked
            // entity are preserved untouched — this is Vasquez H2-v5's core
            // invariant: a stale legacy PUT never clobbers a concurrent modern
            // attention update.
            //
            // A legacy job cell is addressed only when BOTH of its source axes
            // are present: that event toggle and that channel control. Treating
            // either persisted counterpart as request input would let event-only,
            // channel-only, or retention-only patches synthesize row opt-ins.
            bool notifyStart = patch.NotifyOnStart ?? tracked.NotifyOnStart;
            bool notifyComplete = patch.NotifyOnCompletion ?? tracked.NotifyOnCompletion;
            bool notifyFail = patch.NotifyOnFailure ?? tracked.NotifyOnFailure;
            bool notifyPause = patch.NotifyOnPause ?? tracked.NotifyOnPause;

            if (patch.NotifyOnStart.HasValue)
            {
                tracked.NotifyOnStart = notifyStart;
            }

            if (patch.NotifyOnCompletion.HasValue)
            {
                tracked.NotifyOnCompletion = notifyComplete;
            }

            if (patch.NotifyOnFailure.HasValue)
            {
                tracked.NotifyOnFailure = notifyFail;
            }

            if (patch.NotifyOnPause.HasValue)
            {
                tracked.NotifyOnPause = notifyPause;
            }

            // A legacy job cell changes only when both input axes were explicitly
            // supplied. This preserves full legacy cross-product requests while
            // making omitted fields true per-cell no-ops. Attention rows are never
            // rewritten; the independent global control suppresses a whole channel
            // without destroying per-kind choices.
            bool startChanged = patch.NotifyOnStart.HasValue;
            bool completionChanged = patch.NotifyOnCompletion.HasValue;
            bool failureChanged = patch.NotifyOnFailure.HasValue;
            bool pauseChanged = patch.NotifyOnPause.HasValue;

            ApplyLegacyJobCells(
                tracked,
                NotificationDeliveryChannel.InApp,
                patch.EnableInAppNotifications.HasValue,
                startChanged,
                completionChanged,
                failureChanged,
                pauseChanged,
                notifyStart,
                notifyComplete,
                notifyFail,
                notifyPause);
            ApplyLegacyJobCells(
                tracked,
                NotificationDeliveryChannel.Email,
                patch.EnableEmailNotifications.HasValue,
                startChanged,
                completionChanged,
                failureChanged,
                pauseChanged,
                notifyStart,
                notifyComplete,
                notifyFail,
                notifyPause);
            ApplyLegacyJobCells(
                tracked,
                NotificationDeliveryChannel.Push,
                patch.EnablePushNotifications.HasValue,
                startChanged,
                completionChanged,
                failureChanged,
                pauseChanged,
                notifyStart,
                notifyComplete,
                notifyFail,
                notifyPause);
            ApplyLegacyJobCells(
                tracked,
                NotificationDeliveryChannel.Telegram,
                patch.EnableTelegramNotifications.HasValue,
                startChanged,
                completionChanged,
                failureChanged,
                pauseChanged,
                notifyStart,
                notifyComplete,
                notifyFail,
                notifyPause);
        }
        else
        {
            // Modern branch. Each supplied row overwrites the four columns for
            // that event on the tracked entity. Rows omitted from the request
            // are simply not visited, so their persisted value is preserved.
            // Duplicate rows for the same event are last-write-wins within
            // this request (mirrors previous controller behavior).
            // The API wire tokens are the PascalCase enum names: JobStarted,
            // JobCompleted, JobFailed, JobPaused, PrinterFailure,
            // FilamentRunout, HarvestReady, MaintenanceDue, and PrinterOffline.
            //
            // Hicks #3: an empty modern matrix (list with zero rows) reaches
            // this branch too so every row is treated as "omitted" and the
            // caller's request cannot silently reshape rows via the legacy
            // scalar derivation.
            foreach (var row in patch.MatrixRows)
            {
                ApplyRow(tracked, row);
            }

            // Derive the four legacy per-event toggles from the tracked job
            // rows so downstream consumers reading NotifyOn* still see a
            // consistent view. A job row is considered "on" if any of its
            // four channels is enabled.
            tracked.NotifyOnStart = tracked.InAppOnJobStarted || tracked.EmailOnJobStarted || tracked.PushOnJobStarted || tracked.TelegramOnJobStarted;
            tracked.NotifyOnCompletion = tracked.InAppOnJobCompleted || tracked.EmailOnJobCompleted || tracked.PushOnJobCompleted || tracked.TelegramOnJobCompleted;
            tracked.NotifyOnFailure = tracked.InAppOnJobFailed || tracked.EmailOnJobFailed || tracked.PushOnJobFailed || tracked.TelegramOnJobFailed;
            tracked.NotifyOnPause = tracked.InAppOnJobPaused || tracked.EmailOnJobPaused || tracked.PushOnJobPaused || tracked.TelegramOnJobPaused;

            // Legacy contract preservation: in-app job-failed never turns off.
            tracked.InAppOnJobFailed = true;
        }

        // Global channel controls remain whatever the user explicitly selected
        // (or the prior/default value when omitted). Matrix rows cannot reopen a
        // disabled channel.
        tracked.UpdatedAt = DateTime.UtcNow;

        await ctx.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Updated notification preferences for user {UserId} (matrix rows: {RowCount}, new row: {IsNew})",
            userId,
            patch.MatrixRows?.Count ?? 0,
            isNew);

        return tracked;
    }

    private static void ApplyRow(NotificationPreferences tracked, NotificationPreferencesRowPatch row)
    {
        switch (row.EventType)
        {
            case NotificationPreferenceEvent.JobStarted:
                tracked.InAppOnJobStarted = row.InApp;
                tracked.EmailOnJobStarted = row.Email;
                tracked.PushOnJobStarted = row.Push;
                tracked.TelegramOnJobStarted = row.Telegram;
                break;
            case NotificationPreferenceEvent.JobCompleted:
                tracked.InAppOnJobCompleted = row.InApp;
                tracked.EmailOnJobCompleted = row.Email;
                tracked.PushOnJobCompleted = row.Push;
                tracked.TelegramOnJobCompleted = row.Telegram;
                break;
            case NotificationPreferenceEvent.JobFailed:
                // Legacy contract: in-app for job-failed is pinned true. All
                // three other channels are freely toggled.
                tracked.InAppOnJobFailed = true;
                tracked.EmailOnJobFailed = row.Email;
                tracked.PushOnJobFailed = row.Push;
                tracked.TelegramOnJobFailed = row.Telegram;
                break;
            case NotificationPreferenceEvent.JobPaused:
                tracked.InAppOnJobPaused = row.InApp;
                tracked.EmailOnJobPaused = row.Email;
                tracked.PushOnJobPaused = row.Push;
                tracked.TelegramOnJobPaused = row.Telegram;
                break;
            case NotificationPreferenceEvent.PrinterFailure:
                tracked.InAppOnPrinterFailure = row.InApp;
                tracked.EmailOnPrinterFailure = row.Email;
                tracked.PushOnPrinterFailure = row.Push;
                tracked.TelegramOnPrinterFailure = row.Telegram;
                break;
            case NotificationPreferenceEvent.FilamentRunout:
                tracked.InAppOnFilamentRunout = row.InApp;
                tracked.EmailOnFilamentRunout = row.Email;
                tracked.PushOnFilamentRunout = row.Push;
                tracked.TelegramOnFilamentRunout = row.Telegram;
                break;
            case NotificationPreferenceEvent.HarvestReady:
                tracked.InAppOnHarvestReady = row.InApp;
                tracked.EmailOnHarvestReady = row.Email;
                tracked.PushOnHarvestReady = row.Push;
                tracked.TelegramOnHarvestReady = row.Telegram;
                break;
            case NotificationPreferenceEvent.MaintenanceDue:
                tracked.InAppOnMaintenanceDue = row.InApp;
                tracked.EmailOnMaintenanceDue = row.Email;
                tracked.PushOnMaintenanceDue = row.Push;
                tracked.TelegramOnMaintenanceDue = row.Telegram;
                break;
            case NotificationPreferenceEvent.PrinterOffline:
                tracked.InAppOnPrinterOffline = row.InApp;
                tracked.EmailOnPrinterOffline = row.Email;
                tracked.PushOnPrinterOffline = row.Push;
                tracked.TelegramOnPrinterOffline = row.Telegram;
                break;
            default:
                // Unknown enum values cannot reach here — the controller-side
                // JSON binder rejects them with 400 before the service is
                // called. Defensive throw keeps future contract changes noisy.
                throw new ArgumentOutOfRangeException(nameof(row), row.EventType, "Unknown notification preference event.");
        }
    }

    /// <summary>
    /// Hicks #6: attention-category upsert. Wraps the merge/save under a
    /// serializable transaction with fresh-context bounded retry via
    /// <see cref="PreferenceConcurrencyRetry"/>. Every attempt rereads the
    /// persisted map, merges the update using case-insensitive comparison,
    /// enforces cumulative cardinality / UTF-8 byte bounds, and saves
    /// atomically. Concurrent first-creates converge on a single row (loser
    /// retries, rereads the winner's row, merges its own updates on top);
    /// concurrent disjoint-key updates both persist.
    /// </summary>
    public async Task<AttentionCategoryUpdateResult> UpdateAttentionCategoryPreferencesAsync(
        Guid userId,
        IReadOnlyDictionary<string, bool> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);

        // Non-relational (InMemory unit tests): single attempt on the injected
        // context — same semantics minus the retry loop.
        if (!dbContext.Database.IsRelational() || _preferencesContextFactory is null)
        {
            return await ApplyAttentionCategoryUpdateOnContextAsync(dbContext, userId, updates, cancellationToken);
        }

        return await PreferenceConcurrencyRetry.ExecuteAsync(
            _preferencesContextFactory,
            dbContext,
            async (freshContext, ct) =>
            {
                await using var transaction = await BeginPreferenceTransactionAsync(freshContext, ct);
                var result = await ApplyAttentionCategoryUpdateOnContextAsync(freshContext, userId, updates, ct);
                if (result.Status == AttentionCategoryUpdateStatus.Success)
                {
                    await transaction.CommitAsync(ct);
                }
                else
                {
                    // A rejection MUST NOT persist. Rolling back the fresh
                    // context leaves the persisted row byte-for-byte unchanged
                    // even if the (never-mutated) tracked entity is discarded.
                    await transaction.RollbackAsync(ct);
                }

                return result;
            },
            logger,
            cancellationToken);
    }

    private async Task<AttentionCategoryUpdateResult> ApplyAttentionCategoryUpdateOnContextAsync(
        AppDbContext ctx,
        Guid userId,
        IReadOnlyDictionary<string, bool> updates,
        CancellationToken cancellationToken)
    {
        var tracked = await ctx.NotificationPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        if (OnAfterPreferenceReadForTestsAsync is { } hook)
        {
            await hook(ctx, cancellationToken);
        }

        string? preExistingJson = tracked?.AttentionPushCategoryPreferencesJson;

        if (tracked is null)
        {
            // Hicks #5 + #6: first-create MUST use the canonical factory so
            // omitted job/attention rows and email defaults match the
            // fresh-GET contract. The previous inline `new { UserId = ... }`
            // bypass produced a row whose CLR defaults (email = true) then
            // silently enabled email delivery.
            tracked = NotificationPreferencesDefaults.Create(userId);
            tracked.Id = Guid.NewGuid().ToString();
            tracked.CreatedAt = DateTime.UtcNow;
            ctx.NotificationPreferences.Add(tracked);
        }

        AttentionPushCategoryPreferences catPrefs = AttentionPushCategoryPreferences.FromJson(preExistingJson);

        // Merge the caller-supplied updates onto the prospective map.
        // AttentionPushCategoryPreferences uses OrdinalIgnoreCase keys, so
        // "PrinterFailure" and "printerfailure" collapse into a single entry
        // and duplicate-case last-write-wins is honoured within a single
        // request.
        foreach (KeyValuePair<string, bool> kv in updates)
        {
            catPrefs.Categories[kv.Key] = kv.Value;
        }

        // Cumulative bounds — enforced inside the transaction after the
        // merge, so a concurrent request cannot slip a bulk update past a
        // per-request bound applied elsewhere.
        if (catPrefs.Categories.Count > AttentionCategoryCumulativeKeyLimit)
        {
            if (preExistingJson is null && ctx.Entry(tracked).State == EntityState.Added)
            {
                // Detach so a rejection does not accidentally persist an
                // empty first-create row when the caller's update was
                // rejected outright.
                ctx.Entry(tracked).State = EntityState.Detached;
            }

            return AttentionCategoryUpdateResult.FromRejection(AttentionCategoryUpdateRejection.CumulativeKeyLimitExceeded);
        }

        string prospectiveJson = catPrefs.ToJson();
        int prospectiveBytes = System.Text.Encoding.UTF8.GetByteCount(prospectiveJson);
        if (prospectiveBytes > AttentionCategoryCumulativeJsonBytes)
        {
            if (preExistingJson is null && ctx.Entry(tracked).State == EntityState.Added)
            {
                ctx.Entry(tracked).State = EntityState.Detached;
            }

            return AttentionCategoryUpdateResult.FromRejection(AttentionCategoryUpdateRejection.JsonByteLimitExceeded);
        }

        tracked.AttentionPushCategoryPreferencesJson = prospectiveJson;
        tracked.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync(cancellationToken);

        // Return a copy of the persisted map (defensive; the caller must not
        // mutate our internal state) preserving case-insensitive key semantics.
        return AttentionCategoryUpdateResult.FromSuccess(
            new Dictionary<string, bool>(catPrefs.Categories, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Cumulative cardinality cap (Hicks #4 / #6). Kept identical to the
    /// controller-side <c>MaxAttentionCategoryKeysPersisted</c> so both
    /// enforcement points agree on the bound; the service is the
    /// authoritative one under concurrent load because it observes the merged
    /// prospective map inside the transaction.
    /// </summary>
    internal const int AttentionCategoryCumulativeKeyLimit = AttentionPushCategoryPreferences.MaxPersistedKeys;

    /// <summary>
    /// Cumulative UTF-8 byte cap (Hicks #4 / #6). Matches
    /// <c>MaxAttentionCategoryJsonBytes</c> in the controller.
    /// </summary>
    internal const int AttentionCategoryCumulativeJsonBytes = AttentionPushCategoryPreferences.MaxSerializedUtf8Bytes;

    private static Task<ProviderSafeSerializableTransactionScope> BeginPreferenceTransactionAsync(
        AppDbContext context,
        CancellationToken cancellationToken) =>
        ProviderSafeSerializableTransaction.BeginAsync(context, cancellationToken);

    private static void ApplyLegacyJobCells(
        NotificationPreferences preferences,
        NotificationDeliveryChannel channel,
        bool channelChanged,
        bool startChanged,
        bool completionChanged,
        bool failureChanged,
        bool pauseChanged,
        bool notifyStart,
        bool notifyCompletion,
        bool notifyFailure,
        bool notifyPause)
    {
        bool enabled = channel switch
        {
            NotificationDeliveryChannel.InApp => preferences.EnableInAppNotifications,
            NotificationDeliveryChannel.Email => preferences.EnableEmailNotifications,
            NotificationDeliveryChannel.Push => preferences.EnablePushNotifications,
            NotificationDeliveryChannel.Telegram => preferences.EnableTelegramNotifications,
            _ => false,
        };

        if (channelChanged && startChanged)
        {
            SetJobCell(preferences, NotificationPreferenceEvent.JobStarted, channel, enabled && notifyStart);
        }

        if (channelChanged && completionChanged)
        {
            SetJobCell(preferences, NotificationPreferenceEvent.JobCompleted, channel, enabled && notifyCompletion);
        }

        if (channelChanged && failureChanged)
        {
            bool value = channel == NotificationDeliveryChannel.InApp || (enabled && notifyFailure);
            SetJobCell(preferences, NotificationPreferenceEvent.JobFailed, channel, value);
        }

        if (channelChanged && pauseChanged)
        {
            SetJobCell(preferences, NotificationPreferenceEvent.JobPaused, channel, enabled && notifyPause);
        }
    }

    private static void SetJobCell(
        NotificationPreferences preferences,
        NotificationPreferenceEvent eventType,
        NotificationDeliveryChannel channel,
        bool value)
    {
        switch (eventType, channel)
        {
            case (NotificationPreferenceEvent.JobStarted, NotificationDeliveryChannel.InApp):
                preferences.InAppOnJobStarted = value;
                break;
            case (NotificationPreferenceEvent.JobStarted, NotificationDeliveryChannel.Email):
                preferences.EmailOnJobStarted = value;
                break;
            case (NotificationPreferenceEvent.JobStarted, NotificationDeliveryChannel.Push):
                preferences.PushOnJobStarted = value;
                break;
            case (NotificationPreferenceEvent.JobStarted, NotificationDeliveryChannel.Telegram):
                preferences.TelegramOnJobStarted = value;
                break;
            case (NotificationPreferenceEvent.JobCompleted, NotificationDeliveryChannel.InApp):
                preferences.InAppOnJobCompleted = value;
                break;
            case (NotificationPreferenceEvent.JobCompleted, NotificationDeliveryChannel.Email):
                preferences.EmailOnJobCompleted = value;
                break;
            case (NotificationPreferenceEvent.JobCompleted, NotificationDeliveryChannel.Push):
                preferences.PushOnJobCompleted = value;
                break;
            case (NotificationPreferenceEvent.JobCompleted, NotificationDeliveryChannel.Telegram):
                preferences.TelegramOnJobCompleted = value;
                break;
            case (NotificationPreferenceEvent.JobFailed, NotificationDeliveryChannel.InApp):
                preferences.InAppOnJobFailed = true;
                break;
            case (NotificationPreferenceEvent.JobFailed, NotificationDeliveryChannel.Email):
                preferences.EmailOnJobFailed = value;
                break;
            case (NotificationPreferenceEvent.JobFailed, NotificationDeliveryChannel.Push):
                preferences.PushOnJobFailed = value;
                break;
            case (NotificationPreferenceEvent.JobFailed, NotificationDeliveryChannel.Telegram):
                preferences.TelegramOnJobFailed = value;
                break;
            case (NotificationPreferenceEvent.JobPaused, NotificationDeliveryChannel.InApp):
                preferences.InAppOnJobPaused = value;
                break;
            case (NotificationPreferenceEvent.JobPaused, NotificationDeliveryChannel.Email):
                preferences.EmailOnJobPaused = value;
                break;
            case (NotificationPreferenceEvent.JobPaused, NotificationDeliveryChannel.Push):
                preferences.PushOnJobPaused = value;
                break;
            case (NotificationPreferenceEvent.JobPaused, NotificationDeliveryChannel.Telegram):
                preferences.TelegramOnJobPaused = value;
                break;
        }
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
        // Hicks #3: this legacy helper now delegates to the canonical shared
        // defaults so any future default-shape change lands in exactly one
        // place. Kept for backward compatibility with callers outside this
        // file; new code should call NotificationPreferencesDefaults.Create
        // directly.
        return NotificationPreferencesDefaults.Create(userId);
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
            logger.LogWarning("Email delivery failed for user {UserId}: {Error}", user.Id, LogSanitizer.Sanitize(result.Error ?? result.ProviderMessage));
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
                        LogSanitizer.Sanitize(target.Endpoint));
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
                LogSanitizer.Sanitize(failed.Endpoint),
                LogSanitizer.Sanitize(failed.Error));
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
                        LogSanitizer.Sanitize(target.User.Email));
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
                        LogSanitizer.Sanitize(result.Error));
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
            logger.LogInformation("Deleted push subscription for user {UserId} endpoint {Endpoint}", userId, LogSanitizer.Sanitize(endpoint));
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
        return KnownPushServiceHosts.Any(knownHost =>
            string.Equals(host, knownHost, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{knownHost}", StringComparison.OrdinalIgnoreCase));
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
