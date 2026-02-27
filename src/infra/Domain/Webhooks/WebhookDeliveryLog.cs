namespace Farm.Infrastructure.Domain.Webhooks;

/// <summary>
/// Records a webhook delivery attempt for auditing and debugging
/// </summary>
public class WebhookDeliveryLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WebhookSubscriptionId { get; set; }

    public virtual WebhookSubscription? Subscription { get; set; }

    /// <summary>
    /// Event type that triggered this delivery (e.g. "job.completed")
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// JSON payload that was sent
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// HTTP status code returned (null if request failed)
    /// </summary>
    public int? StatusCode { get; set; }

    /// <summary>
    /// Whether delivery was successful (2xx status)
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if delivery failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Which attempt this was (1-based)
    /// </summary>
    public int Attempt { get; set; } = 1;

    /// <summary>
    /// Round-trip time in milliseconds
    /// </summary>
    public long? DurationMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
