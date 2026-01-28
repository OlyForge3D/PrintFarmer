using Farm.Infrastructure.Domain.Notifications;

namespace Farm.Shared.Models.Dtos.Notifications;

/// <summary>
/// Data transfer object for a user notification.
/// Represents a single notification message with metadata about delivery and read status.
/// </summary>
public class NotificationDto
{
    /// <summary>Gets or sets the unique identifier for this notification.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the user ID this notification is for.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional job ID this notification relates to.</summary>
    public string? JobId { get; set; }

    /// <summary>Gets or sets the type of notification (e.g., completion, failure, warning).</summary>
    public NotificationType Type { get; set; }

    /// <summary>Gets or sets the notification subject line.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the notification body content.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Gets or sets optional JSON metadata for additional context.</summary>
    public string? Metadata { get; set; }

    /// <summary>Gets or sets a value indicating whether the notification has been read.</summary>
    public bool IsRead { get; set; }

    /// <summary>Gets or sets when this notification was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets when this notification was read, if applicable.</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>Gets or sets when this notification expires, if applicable.</summary>
    public DateTime? ExpiresAt { get; set; }
}
