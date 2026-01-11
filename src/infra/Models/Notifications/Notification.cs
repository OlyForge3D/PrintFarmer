namespace Farm.Infrastructure.Domain.Notifications;

/// <summary>
/// Represents a user notification for job events and system alerts
/// </summary>
public class Notification
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User receiving the notification
    /// </summary>
    public Guid UserId { get; set; }
    public virtual User? User { get; set; }

    /// <summary>
    /// Associated job (if notification is about a specific job)
    /// </summary>
    public Guid? JobId { get; set; }
    public virtual PrintJob? Job { get; set; }

    /// <summary>
    /// Type of notification (JobCompleted, JobFailed, etc.)
    /// </summary>
    public NotificationType Type { get; set; }

    /// <summary>
    /// Notification subject/title
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Notification body/message
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Optional additional data (JSON)
    /// </summary>
    public string? Metadata { get; set; }

    /// <summary>
    /// Whether notification has been read by user
    /// </summary>
    public bool IsRead { get; set; } = false;

    /// <summary>
    /// When notification was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When notification was read by user
    /// </summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>
    /// When notification should be deleted
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}
