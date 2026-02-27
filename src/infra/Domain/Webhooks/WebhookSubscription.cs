namespace Farm.Infrastructure.Domain.Webhooks;

/// <summary>
/// Represents a webhook subscription that receives event notifications via HTTP POST
/// </summary>
public class WebhookSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable name for this webhook
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Target URL that receives HTTP POST payloads
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Shared secret for HMAC-SHA256 payload signing (X-Webhook-Signature header)
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Comma-separated event types this webhook subscribes to (e.g. "job.completed,job.failed")
    /// Use "*" to subscribe to all events
    /// </summary>
    public string EventTypes { get; set; } = "*";

    /// <summary>
    /// Whether this webhook is currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Number of consecutive delivery failures
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Auto-disable after this many consecutive failures (0 = never disable)
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 10;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastDeliveryAt { get; set; }

    public DateTime? LastSuccessAt { get; set; }
}
