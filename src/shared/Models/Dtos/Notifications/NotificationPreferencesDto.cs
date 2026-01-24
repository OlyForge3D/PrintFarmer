using Farm.Infrastructure.Domain.Notifications;

namespace Farm.Shared.Models.Dtos.Notifications;

/// <summary>
/// Data transfer object for user notification preferences.
/// Contains settings for notification delivery channels and event triggers.
/// </summary>
public class NotificationPreferencesDto
{
    /// <summary>Gets or sets the unique identifier for these preferences.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the user ID these preferences belong to.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether email notifications are enabled.</summary>
    public bool EnableEmailNotifications { get; set; }

    /// <summary>Gets or sets a value indicating whether push notifications are enabled.</summary>
    public bool EnablePushNotifications { get; set; }

    /// <summary>Gets or sets a value indicating whether in-app notifications are enabled.</summary>
    public bool EnableInAppNotifications { get; set; }

    /// <summary>Gets or sets a value indicating whether to notify on print job completion.</summary>
    public bool NotifyOnCompletion { get; set; }

    /// <summary>Gets or sets a value indicating whether to notify on print job failure.</summary>
    public bool NotifyOnFailure { get; set; }

    /// <summary>Gets or sets a value indicating whether to notify on print job start.</summary>
    public bool NotifyOnStart { get; set; }

    /// <summary>Gets or sets a value indicating whether to notify on print job pause.</summary>
    public bool NotifyOnPause { get; set; }

    /// <summary>Gets or sets the frequency of notification delivery.</summary>
    public NotificationFrequency Frequency { get; set; }

    /// <summary>Gets or sets the number of days to retain notifications.</summary>
    public int RetentionDays { get; set; }

    /// <summary>Gets or sets when these preferences were created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets when these preferences were last updated.</summary>
    public DateTime UpdatedAt { get; set; }
}
