using Farm.Infrastructure.Domain.Notifications;

namespace Farm.Shared.Models.Dtos.Notifications;

public class NotificationPreferencesDto
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public bool EnableEmailNotifications { get; set; }
    public bool EnablePushNotifications { get; set; }
    public bool EnableInAppNotifications { get; set; }
    public bool NotifyOnCompletion { get; set; }
    public bool NotifyOnFailure { get; set; }
    public bool NotifyOnStart { get; set; }
    public bool NotifyOnPause { get; set; }
    public NotificationFrequency Frequency { get; set; }
    public int RetentionDays { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
