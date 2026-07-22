namespace Farm.Infrastructure.Domain.Notifications;

/// <summary>
/// User preferences for notifications (email, push, in-app)
/// </summary>
public class NotificationPreferences
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User these preferences belong to
    /// </summary>
    public Guid UserId { get; set; }

    public virtual User? User { get; set; }

    /// <summary>
    /// Enable email notifications
    /// </summary>
    public bool EnableEmailNotifications { get; set; } = true;

    /// <summary>
    /// Enable push notifications (web push / mobile)
    /// </summary>
    public bool EnablePushNotifications { get; set; } = true;

    /// <summary>
    /// Enable in-app notifications
    /// </summary>
    public bool EnableInAppNotifications { get; set; } = true;

    /// <summary>
    /// Enable Telegram notifications
    /// </summary>
    public bool EnableTelegramNotifications { get; set; } = false;

    /// <summary>
    /// Notify when job completes
    /// </summary>
    public bool NotifyOnCompletion { get; set; } = true;

    /// <summary>
    /// Notify when job fails or encounters errors
    /// </summary>
    public bool NotifyOnFailure { get; set; } = true;

    /// <summary>
    /// Notify when job starts printing
    /// </summary>
    public bool NotifyOnStart { get; set; } = false;

    /// <summary>
    /// Notify when job is paused
    /// </summary>
    public bool NotifyOnPause { get; set; } = true;

    /// <summary>
    /// Enable in-app notification delivery for job start events.
    /// </summary>
    public bool InAppOnJobStarted { get; set; } = false;

    /// <summary>
    /// Enable in-app notification delivery for job completion events.
    /// </summary>
    public bool InAppOnJobCompleted { get; set; } = true;

    /// <summary>
    /// Enable in-app notification delivery for job failure events.
    /// Always enforced as enabled by service policy.
    /// </summary>
    public bool InAppOnJobFailed { get; set; } = true;

    /// <summary>
    /// Enable in-app notification delivery for job pause/resume events.
    /// </summary>
    public bool InAppOnJobPaused { get; set; } = true;

    /// <summary>
    /// Enable email delivery for job start events.
    /// </summary>
    public bool EmailOnJobStarted { get; set; } = false;

    /// <summary>
    /// Enable email delivery for job completion events.
    /// </summary>
    public bool EmailOnJobCompleted { get; set; } = true;

    /// <summary>
    /// Enable email delivery for job failure events.
    /// </summary>
    public bool EmailOnJobFailed { get; set; } = true;

    /// <summary>
    /// Enable email delivery for job pause/resume events.
    /// </summary>
    public bool EmailOnJobPaused { get; set; } = true;

    /// <summary>
    /// Enable push delivery for job start events.
    /// </summary>
    public bool PushOnJobStarted { get; set; } = false;

    /// <summary>
    /// Enable push delivery for job completion events.
    /// </summary>
    public bool PushOnJobCompleted { get; set; } = true;

    /// <summary>
    /// Enable push delivery for job failure events.
    /// </summary>
    public bool PushOnJobFailed { get; set; } = true;

    /// <summary>
    /// Enable push delivery for job pause/resume events.
    /// </summary>
    public bool PushOnJobPaused { get; set; } = true;

    /// <summary>
    /// Enable Telegram delivery for job start events.
    /// </summary>
    public bool TelegramOnJobStarted { get; set; } = false;

    /// <summary>
    /// Enable Telegram delivery for job completion events.
    /// </summary>
    public bool TelegramOnJobCompleted { get; set; } = false;

    /// <summary>
    /// Enable Telegram delivery for job failure events.
    /// </summary>
    public bool TelegramOnJobFailed { get; set; } = false;

    /// <summary>
    /// Enable Telegram delivery for job pause/resume events.
    /// </summary>
    public bool TelegramOnJobPaused { get; set; } = false;

    /// <summary>
    /// Notification frequency (real-time, hourly digest, daily digest)
    /// </summary>
    public NotificationFrequency Frequency { get; set; } = NotificationFrequency.RealTime;

    /// <summary>
    /// Keep notifications for this many days
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// When preferences were created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When preferences were last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool IsChannelEnabled(NotificationType type, NotificationDeliveryChannel channel)
    {
        NotificationType normalizedType = type == NotificationType.JobResumed ? NotificationType.JobPaused : type;

        if (normalizedType == NotificationType.JobFailed && channel == NotificationDeliveryChannel.InApp)
        {
            return true;
        }

        return (normalizedType, channel) switch
        {
            (NotificationType.JobStarted, NotificationDeliveryChannel.InApp) => InAppOnJobStarted,
            (NotificationType.JobCompleted, NotificationDeliveryChannel.InApp) => InAppOnJobCompleted,
            (NotificationType.JobFailed, NotificationDeliveryChannel.InApp) => InAppOnJobFailed,
            (NotificationType.JobPaused, NotificationDeliveryChannel.InApp) => InAppOnJobPaused,
            (NotificationType.JobStarted, NotificationDeliveryChannel.Email) => EmailOnJobStarted,
            (NotificationType.JobCompleted, NotificationDeliveryChannel.Email) => EmailOnJobCompleted,
            (NotificationType.JobFailed, NotificationDeliveryChannel.Email) => EmailOnJobFailed,
            (NotificationType.JobPaused, NotificationDeliveryChannel.Email) => EmailOnJobPaused,
            (NotificationType.JobStarted, NotificationDeliveryChannel.Push) => PushOnJobStarted,
            (NotificationType.JobCompleted, NotificationDeliveryChannel.Push) => PushOnJobCompleted,
            (NotificationType.JobFailed, NotificationDeliveryChannel.Push) => PushOnJobFailed,
            (NotificationType.JobPaused, NotificationDeliveryChannel.Push) => PushOnJobPaused,
            (NotificationType.JobStarted, NotificationDeliveryChannel.Telegram) => TelegramOnJobStarted,
            (NotificationType.JobCompleted, NotificationDeliveryChannel.Telegram) => TelegramOnJobCompleted,
            (NotificationType.JobFailed, NotificationDeliveryChannel.Telegram) => TelegramOnJobFailed,
            (NotificationType.JobPaused, NotificationDeliveryChannel.Telegram) => TelegramOnJobPaused,
            _ => false
        };
    }
}

public enum NotificationDeliveryChannel
{
    InApp,
    Email,
    Push,
    Telegram
}

public enum NotificationFrequency
{
    RealTime,      // Immediate notification
    Hourly,        // Hourly digest
    Daily,         // Daily digest
    Weekly,        // Weekly digest
    Never           // Disabled
}
