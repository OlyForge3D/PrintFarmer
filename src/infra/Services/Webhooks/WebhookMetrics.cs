using System.Diagnostics.Metrics;

namespace Farm.Infrastructure.Services.Webhooks;

/// <summary>
/// System.Diagnostics.Metrics facade for <see cref="WebhookService"/>'s delivery pipeline.
/// Instrument names and the bounded tag schema are documented in <c>docs/WEBHOOKS.md</c>.
/// Tags never carry subscription URLs, IDs, secrets, or event payloads.
/// </summary>
public sealed class WebhookMetrics : IDisposable
{
    /// <summary>Meter name used by OpenTelemetry subscribers.</summary>
    public const string MeterName = "Farm.Infrastructure.Services.Webhooks";

    /// <summary>Tag key for the outcome of a delivery attempt.</summary>
    public const string OutcomeTag = "outcome";

    /// <summary>Tag key for the reason an event or delivery was dropped.</summary>
    public const string ReasonTag = "reason";

    /// <summary>Attempt outcome: 2xx response.</summary>
    public const string OutcomeSuccess = "success";

    /// <summary>Attempt outcome: non-2xx response.</summary>
    public const string OutcomeHttpError = "http_error";

    /// <summary>Attempt outcome: HttpClient timeout elapsed.</summary>
    public const string OutcomeTimeout = "timeout";

    /// <summary>Attempt outcome: DNS, connection, TLS or other transport failure.</summary>
    public const string OutcomeNetworkError = "network_error";

    /// <summary>Attempt outcome: destination rejected by the private/reserved address check.</summary>
    public const string OutcomeBlockedDestination = "blocked_destination";

    /// <summary>Drop reason: in-memory event queue was full.</summary>
    public const string ReasonQueueFull = "queue_full";

    /// <summary>Drop reason: pending-retry buffer was full, so the retry was not scheduled.</summary>
    public const string ReasonRetryQueueFull = "retry_queue_full";

    /// <summary>Drop reason: subscription was deleted before a scheduled retry ran.</summary>
    public const string ReasonSubscriptionRemoved = "subscription_removed";

    /// <summary>Drop reason: subscription was disabled before a scheduled retry ran.</summary>
    public const string ReasonSubscriptionInactive = "subscription_inactive";

    /// <summary>Drop reason: subscription no longer subscribes to the event type.</summary>
    public const string ReasonEventTypeUnsubscribed = "event_type_unsubscribed";

    private readonly Meter _meter;

    /// <summary>Number of events waiting in the in-memory event queue (up/down counter).</summary>
    public UpDownCounter<int> QueueDepth { get; }

    /// <summary>Number of failed deliveries waiting for a scheduled retry (up/down counter).</summary>
    public UpDownCounter<int> PendingRetries { get; }

    /// <summary>Age of an event when the drain loop dequeues it, in milliseconds.</summary>
    public Histogram<double> QueueAgeMs { get; }

    /// <summary>Duration of each delivery attempt, including timeouts. Tag: <c>outcome</c>.</summary>
    public Histogram<double> AttemptDurationMs { get; }

    /// <summary>Events dropped before delivery was attempted. Tag: <c>reason</c>.</summary>
    public Counter<long> EventsDropped { get; }

    /// <summary>Per-subscription deliveries abandoned before retries finished. Tag: <c>reason</c>.</summary>
    public Counter<long> DeliveriesDropped { get; }

    /// <summary>Deliveries that used every attempt without a 2xx response.</summary>
    public Counter<long> DeliveriesExhausted { get; }

    /// <summary>Unexpected per-delivery exceptions isolated so later subscribers still run.</summary>
    public Counter<long> IsolatedDeliveryFailures { get; }

    /// <summary>Constructs the meter and instruments.</summary>
    public WebhookMetrics()
    {
        _meter = new Meter(MeterName);
        QueueDepth = _meter.CreateUpDownCounter<int>(
            "webhook.queue_depth",
            description: "Events waiting in the in-memory webhook queue");
        PendingRetries = _meter.CreateUpDownCounter<int>(
            "webhook.pending_retries",
            description: "Failed deliveries waiting for a scheduled retry");
        QueueAgeMs = _meter.CreateHistogram<double>(
            "webhook.queue_age_ms",
            unit: "ms",
            description: "Age of a webhook event when the drain loop dequeues it");
        AttemptDurationMs = _meter.CreateHistogram<double>(
            "webhook.attempt_duration_ms",
            unit: "ms",
            description: "Duration of a single webhook delivery attempt, including timeouts");
        EventsDropped = _meter.CreateCounter<long>(
            "webhook.events_dropped",
            description: "Webhook events dropped before delivery");
        DeliveriesDropped = _meter.CreateCounter<long>(
            "webhook.deliveries_dropped",
            description: "Webhook deliveries abandoned before all retries ran");
        DeliveriesExhausted = _meter.CreateCounter<long>(
            "webhook.deliveries_exhausted",
            description: "Webhook deliveries that failed every attempt");
        IsolatedDeliveryFailures = _meter.CreateCounter<long>(
            "webhook.isolated_delivery_failures",
            description: "Unexpected per-delivery exceptions isolated from other subscribers");
    }

    /// <summary>The underlying meter; exposed so tests can filter by instance.</summary>
    internal Meter Meter => _meter;

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
