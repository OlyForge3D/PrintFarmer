namespace Farm.Infrastructure.Domain.Notifications;

/// <summary>
/// Stores a user's web push subscription for delivering browser notifications.
/// </summary>
public class PushSubscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public Guid UserId { get; set; }

    public virtual User? User { get; set; }

    /// <summary>
    /// The push subscription endpoint URL provided by the browser.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// The p256dh key from the browser subscription (Base64url encoded).
    /// </summary>
    public string P256dh { get; set; } = string.Empty;

    /// <summary>
    /// The auth secret from the browser subscription (Base64url encoded).
    /// </summary>
    public string Auth { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }
}
