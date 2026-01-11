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
}

public enum NotificationFrequency
{
    RealTime,      // Immediate notification
    Hourly,        // Hourly digest
    Daily,         // Daily digest
    Weekly,        // Weekly digest
    Never           // Disabled
}
