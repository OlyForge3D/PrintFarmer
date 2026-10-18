using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Data.Interceptors;
using Farm.Infrastructure.Domain.Webhooks;
using Farm.Infrastructure.Network;
using Farm.Infrastructure.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Webhooks;

/// <summary>
/// Dispatches webhook events to matching subscriptions via a background queue
/// </summary>
public interface IWebhookService
{
    /// <summary>
    /// Enqueue an event for delivery to all matching webhook subscriptions
    /// </summary>
    void Enqueue(string eventType, object payload);
}

/// <summary>
/// Manages webhook delivery with HMAC signing, retries, and failure tracking.
/// </summary>
/// <remarks>
/// A single drain loop delivers one attempt at a time, so no two deliveries ever run
/// concurrently and each attempt uses its own DI scope and <see cref="AppDbContext"/>.
/// Failed attempts are rescheduled into a bounded, in-memory retry buffer instead of
/// sleeping inline, so retry backoff no longer stalls later events or subscribers.
/// Like the event queue, pending retries are lost on restart. Delivery order across
/// events is therefore not guaranteed for a subscriber that needs a retry; see
/// <c>docs/WEBHOOKS.md</c>.
/// </remarks>
public sealed class WebhookService : BackgroundService, IWebhookService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private const int MaxRetries = 3;

    private static readonly TimeSpan DeliveryLogRetention = TimeSpan.FromDays(90);
    private DateTime _lastCleanup = DateTime.MinValue;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISensitiveDataProtector _sensitiveDataProtector;
    private readonly ILogger<WebhookService> _logger;
    private readonly WebhookMetrics _metrics;
    private readonly WebhookDeliveryTuning _tuning;

    private readonly ConcurrentQueue<WebhookEvent> _queue = new();

    // Only the drain loop (ExecuteAsync) reads or writes the retry buffer, so it needs no locking.
    private readonly PriorityQueue<PendingDelivery, (DateTime DueAtUtc, long Sequence)> _pendingRetries = new();
    private long _retrySequence;

    /// <summary>
    /// Creates the service with production delivery settings.
    /// </summary>
    public WebhookService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ISensitiveDataProtector sensitiveDataProtector,
        ILogger<WebhookService> logger,
        WebhookMetrics metrics)
        : this(scopeFactory, httpClientFactory, sensitiveDataProtector, logger, metrics, WebhookDeliveryTuning.Default)
    {
    }

    internal WebhookService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ISensitiveDataProtector sensitiveDataProtector,
        ILogger<WebhookService> logger,
        WebhookMetrics metrics,
        WebhookDeliveryTuning tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        if (tuning.RetryDelays.Count < MaxRetries - 1)
        {
            throw new ArgumentException($"At least {MaxRetries - 1} retry delays are required.", nameof(tuning));
        }

        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _sensitiveDataProtector = sensitiveDataProtector;
        _logger = logger;
        _metrics = metrics;
        _tuning = tuning;
    }

    /// <inheritdoc />
    public void Enqueue(string eventType, object payload)
    {
        if (_queue.Count >= _tuning.MaxQueueSize)
        {
            _logger.LogWarning("Webhook queue full ({MaxSize}), dropping event {EventType}", _tuning.MaxQueueSize, eventType);
            _metrics.EventsDropped.Add(1, new KeyValuePair<string, object?>(WebhookMetrics.ReasonTag, WebhookMetrics.ReasonQueueFull));
            return;
        }

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        // Count before publishing so the drain loop's decrement can never precede this increment.
        _metrics.QueueDepth.Add(1);
        _queue.Enqueue(new WebhookEvent(eventType, json, DateTime.UtcNow));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookService started");

        // Alternate between due retries and new events so neither can starve the other.
        bool retryTurn = true;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (retryTurn && TryTakeDueRetry(out PendingDelivery? retry))
                {
                    retryTurn = false;
                    await RunDeliveryAttemptAsync(retry, null, stoppingToken);
                    continue;
                }

                if (_queue.TryDequeue(out var evt))
                {
                    retryTurn = true;
                    _metrics.QueueDepth.Add(-1);
                    _metrics.QueueAgeMs.Record(Math.Max(0, (DateTime.UtcNow - evt.Timestamp).TotalMilliseconds));

                    try
                    {
                        await ProcessEventAsync(evt, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Error processing webhook event {EventType}", evt.EventType);
                    }

                    continue;
                }

                if (TryTakeDueRetry(out retry))
                {
                    await RunDeliveryAttemptAsync(retry, null, stoppingToken);
                    continue;
                }

                // Periodically clean up old delivery logs during idle time
                await CleanupOldDeliveryLogsAsync(stoppingToken);
                await Task.Delay(GetIdleDelay(), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown requested; fall through to the discard report below.
        }
        catch (Exception ex) when (stoppingToken.IsCancellationRequested)
        {
            // An in-flight attempt can fail with a non-cancellation error (e.g. a disposed
            // dependency) while the host is stopping; that is part of a normal stop.
            _logger.LogInformation(ex, "Webhook delivery interrupted by shutdown");
        }
        finally
        {
            ReportDiscardedOnShutdown();
        }
    }

    private void ReportDiscardedOnShutdown()
    {
        int queuedEvents = 0;
        while (_queue.TryDequeue(out _))
        {
            queuedEvents++;
        }

        int pendingRetries = _pendingRetries.Count;
        _pendingRetries.Clear();

        _metrics.QueueDepth.Add(-queuedEvents);
        _metrics.PendingRetries.Add(-pendingRetries);

        if (queuedEvents > 0 || pendingRetries > 0)
        {
            _logger.LogInformation(
                "WebhookService stopping: discarding {QueuedEvents} queued webhook events and {PendingRetries} pending webhook retries; they are not persisted and will not be delivered",
                queuedEvents, pendingRetries);
        }
    }

    private async Task ProcessEventAsync(WebhookEvent evt, CancellationToken ct)
    {
        List<WebhookSubscription> matching;

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var subscriptions = await db.WebhookSubscriptions
                .Where(w => w.IsActive)
                .AsNoTracking()
                .ToListAsync(ct);

            matching = subscriptions
                .Where(w => MatchesEventType(w.EventTypes, evt.EventType))
                .ToList();
        }

        if (matching.Count == 0)
        {
            return;
        }

        _logger.LogDebug(
            "Delivering webhook event {EventType} to {Count} subscriptions",
            evt.EventType, matching.Count);

        foreach (WebhookSubscription sub in matching)
        {
            var delivery = new PendingDelivery(sub.Id, evt, BuildEnvelope(evt), Attempt: 1);
            await RunDeliveryAttemptAsync(delivery, sub, ct);
        }
    }

    private static string BuildEnvelope(WebhookEvent evt) =>
        JsonSerializer.Serialize(
            new
            {
                id = Guid.NewGuid().ToString(),
                @event = evt.EventType,
                timestamp = evt.Timestamp,
                data = JsonSerializer.Deserialize<JsonElement>(evt.PayloadJson)
            }, JsonOptions);

    /// <summary>
    /// Runs one delivery attempt in its own DI scope. Any non-cancellation exception is
    /// contained here so one subscriber cannot skip the remaining subscribers or events.
    /// </summary>
    private async Task RunDeliveryAttemptAsync(
        PendingDelivery delivery,
        WebhookSubscription? snapshot,
        CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Retries re-read the subscription so disable, delete, event-type and URL/secret
            // changes made since the previous attempt are honoured.
            WebhookSubscription? subscription = snapshot ?? await db.WebhookSubscriptions
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == delivery.SubscriptionId, ct);

            string? abandonReason = subscription switch
            {
                null => WebhookMetrics.ReasonSubscriptionRemoved,
                { IsActive: false } => WebhookMetrics.ReasonSubscriptionInactive,
                _ when !MatchesEventType(subscription.EventTypes, delivery.Event.EventType) => WebhookMetrics.ReasonEventTypeUnsubscribed,
                _ => null,
            };

            if (abandonReason is not null)
            {
                _logger.LogInformation(
                    "Abandoning webhook retry {Attempt} of {EventType} for subscription {SubscriptionId}: {Reason}",
                    delivery.Attempt, delivery.Event.EventType, delivery.SubscriptionId, abandonReason);
                _metrics.DeliveriesDropped.Add(1, new KeyValuePair<string, object?>(WebhookMetrics.ReasonTag, abandonReason));
                return;
            }

            AttemptResult result = await SendAttemptAsync(subscription!, delivery, db, ct);

            switch (result)
            {
                case AttemptResult.Succeeded:
                    // Reset failure counter on success
                    await UpdateSubscriptionStatusAsync(db, delivery.SubscriptionId, true, ct);
                    break;

                case AttemptResult.Blocked:
                    // Unsafe destinations are not retried; count the failure immediately.
                    await UpdateSubscriptionStatusAsync(db, delivery.SubscriptionId, false, ct);
                    break;

                case AttemptResult.Failed when delivery.Attempt < MaxRetries:
                    await ScheduleRetryAsync(delivery, db, ct);
                    break;

                case AttemptResult.Failed:
                    // All retries exhausted — increment failure counter
                    _metrics.DeliveriesExhausted.Add(1);
                    await UpdateSubscriptionStatusAsync(db, delivery.SubscriptionId, false, ct);
                    break;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _metrics.IsolatedDeliveryFailures.Add(1);
            _logger.LogError(
                ex,
                "Unexpected error delivering webhook event {EventType} (attempt {Attempt}) to subscription {SubscriptionId}",
                delivery.Event.EventType, delivery.Attempt, delivery.SubscriptionId);
        }
    }

    private async Task ScheduleRetryAsync(PendingDelivery delivery, AppDbContext db, CancellationToken ct)
    {
        if (_pendingRetries.Count >= _tuning.MaxPendingRetries)
        {
            // No retry capacity left: the delivery is given up and counted as a failed delivery,
            // matching what exhausting its retries would have done.
            _logger.LogWarning(
                "Webhook retry buffer full ({MaxPending}); giving up on {EventType} for subscription {SubscriptionId} after attempt {Attempt}",
                _tuning.MaxPendingRetries, delivery.Event.EventType, delivery.SubscriptionId, delivery.Attempt);
            _metrics.DeliveriesDropped.Add(1, new KeyValuePair<string, object?>(WebhookMetrics.ReasonTag, WebhookMetrics.ReasonRetryQueueFull));
            await UpdateSubscriptionStatusAsync(db, delivery.SubscriptionId, false, ct);
            return;
        }

        DateTime dueAtUtc = DateTime.UtcNow + _tuning.RetryDelays[delivery.Attempt - 1];
        _pendingRetries.Enqueue(delivery with { Attempt = delivery.Attempt + 1 }, (dueAtUtc, _retrySequence++));
        _metrics.PendingRetries.Add(1);
    }

    private bool TryTakeDueRetry([NotNullWhen(true)] out PendingDelivery? retry)
    {
        if (_pendingRetries.TryPeek(out _, out var priority) && priority.DueAtUtc <= DateTime.UtcNow)
        {
            retry = _pendingRetries.Dequeue();
            _metrics.PendingRetries.Add(-1);
            return true;
        }

        retry = null;
        return false;
    }

    private TimeSpan GetIdleDelay()
    {
        if (!_pendingRetries.TryPeek(out _, out var priority))
        {
            return _tuning.IdlePollInterval;
        }

        TimeSpan untilDue = priority.DueAtUtc - DateTime.UtcNow;
        if (untilDue <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(1);
        }

        return untilDue < _tuning.IdlePollInterval ? untilDue : _tuning.IdlePollInterval;
    }

    /// <summary>
    /// Performs one HTTP attempt and records its delivery log and latency metric.
    /// An HttpClient timeout is a failed attempt; cancellation of <paramref name="ct"/>
    /// propagates without being recorded.
    /// </summary>
    private async Task<AttemptResult> SendAttemptAsync(
        WebhookSubscription subscription,
        PendingDelivery delivery,
        AppDbContext db,
        CancellationToken ct)
    {
        var log = new WebhookDeliveryLog
        {
            WebhookSubscriptionId = subscription.Id,
            EventType = delivery.Event.EventType,
            Payload = delivery.Envelope,
            Attempt = delivery.Attempt
        };

        string outcome;
        var sw = Stopwatch.StartNew();

        try
        {
            // SSRF protection: resolve hostname and reject private/reserved IPs
            if (!await IsUrlSafeAsync(subscription.Url, ct))
            {
                sw.Stop();
                log.DurationMs = sw.ElapsedMilliseconds;
                log.Success = false;
                log.ErrorMessage = "URL resolves to a private or reserved IP address";
                RecordAttempt(sw, WebhookMetrics.OutcomeBlockedDestination);
                db.WebhookDeliveryLogs.Add(log);
                await db.SaveChangesAsync(ct);
                return AttemptResult.Blocked;
            }

            using var client = _httpClientFactory.CreateClient("WebhookDelivery");
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url);
            request.Content = new StringContent(delivery.Envelope, Encoding.UTF8, "application/json");

            // HMAC-SHA256 signature (decrypt secret from encrypted storage)
            if (!string.IsNullOrEmpty(subscription.Secret))
            {
                string? plaintextSecret = SensitiveDataEncryptionInterceptor.IsAlreadyEncrypted(subscription.Secret)
                    ? _sensitiveDataProtector.Unprotect(subscription.Secret)
                    : subscription.Secret;

                if (!string.IsNullOrEmpty(plaintextSecret))
                {
                    var hash = HMACSHA256.HashData(
                        Encoding.UTF8.GetBytes(plaintextSecret),
                        Encoding.UTF8.GetBytes(delivery.Envelope));
                    request.Headers.Add(
                        "X-Webhook-Signature",
                        $"sha256={Convert.ToHexStringLower(hash)}");
                }
            }

            request.Headers.Add("X-Webhook-Event", delivery.Event.EventType);

            using var response = await client.SendAsync(request, ct);
            sw.Stop();

            log.StatusCode = (int)response.StatusCode;
            log.DurationMs = sw.ElapsedMilliseconds;
            log.Success = response.IsSuccessStatusCode;

            if (!response.IsSuccessStatusCode)
            {
                log.ErrorMessage = $"HTTP {(int)response.StatusCode}";
            }

            outcome = response.IsSuccessStatusCode ? WebhookMetrics.OutcomeSuccess : WebhookMetrics.OutcomeHttpError;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // An OperationCanceledException while ct is not cancelled is HttpClient.Timeout elapsing.
            sw.Stop();
            log.DurationMs = sw.ElapsedMilliseconds;
            log.Success = false;
            log.ErrorMessage = ex.Message.Length > 1024
                ? ex.Message[..1024]
                : ex.Message;
            outcome = ex is OperationCanceledException ? WebhookMetrics.OutcomeTimeout : WebhookMetrics.OutcomeNetworkError;
        }

        RecordAttempt(sw, outcome);
        db.WebhookDeliveryLogs.Add(log);
        await db.SaveChangesAsync(ct);

        return log.Success ? AttemptResult.Succeeded : AttemptResult.Failed;
    }

    private void RecordAttempt(Stopwatch sw, string outcome) =>
        _metrics.AttemptDurationMs.Record(
            sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>(WebhookMetrics.OutcomeTag, outcome));

    private static async Task UpdateSubscriptionStatusAsync(
        AppDbContext db, Guid subscriptionId, bool success, CancellationToken ct)
    {
        var sub = await db.WebhookSubscriptions.FindAsync([subscriptionId], ct);
        if (sub is null)
        {
            return;
        }

        sub.LastDeliveryAt = DateTime.UtcNow;

        if (success)
        {
            sub.LastSuccessAt = DateTime.UtcNow;
            sub.ConsecutiveFailures = 0;
        }
        else
        {
            sub.ConsecutiveFailures++;
            if (sub.MaxConsecutiveFailures > 0 &&
                sub.ConsecutiveFailures >= sub.MaxConsecutiveFailures)
            {
                sub.IsActive = false;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool MatchesEventType(string subscribed, string eventType)
    {
        if (subscribed == "*")
        {
            return true;
        }

        var types = subscribed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return types.Contains(eventType, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsUrlSafeAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        IPAddress[] addresses;
        addresses = IPAddress.TryParse(uri.Host, out IPAddress? directIp)
            ? [directIp]
            : await Dns.GetHostAddressesAsync(uri.Host, ct);

        return addresses.Length > 0 && addresses.All(ip => !NetworkDestinationClassifier.IsPrivateOrReserved(ip));
    }

    private async Task CleanupOldDeliveryLogsAsync(CancellationToken ct)
    {
        // Run cleanup at most once per hour
        if (DateTime.UtcNow - _lastCleanup < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastCleanup = DateTime.UtcNow;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var cutoff = DateTime.UtcNow - DeliveryLogRetention;
            int deleted = await db.WebhookDeliveryLogs
                .Where(d => d.CreatedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
            {
                _logger.LogInformation("Cleaned up {Count} webhook delivery logs older than {Days} days", deleted, DeliveryLogRetention.TotalDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up old webhook delivery logs");
        }
    }

    private sealed record WebhookEvent(string EventType, string PayloadJson, DateTime Timestamp);

    /// <summary>
    /// One subscription's delivery of one event. <see cref="Envelope"/> (including its
    /// <c>id</c>) is built once and reused for every attempt.
    /// </summary>
    private sealed record PendingDelivery(Guid SubscriptionId, WebhookEvent Event, string Envelope, int Attempt);

    private enum AttemptResult
    {
        Succeeded,
        Failed,
        Blocked,
    }
}

/// <summary>
/// Delivery limits for <see cref="WebhookService"/>. Production uses <see cref="Default"/>;
/// tests shorten the delays and bounds.
/// </summary>
/// <param name="RetryDelays">Backoff before attempt N+1, indexed by N-1.</param>
/// <param name="MaxPendingRetries">Upper bound on deliveries waiting for a retry.</param>
/// <param name="MaxQueueSize">Upper bound on queued, not-yet-processed events.</param>
/// <param name="IdlePollInterval">Maximum sleep when there is no due work.</param>
internal sealed record WebhookDeliveryTuning(
    IReadOnlyList<TimeSpan> RetryDelays,
    int MaxPendingRetries,
    int MaxQueueSize,
    TimeSpan IdlePollInterval)
{
    public static WebhookDeliveryTuning Default { get; } = new(
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)],
        MaxPendingRetries: 1_000,
        MaxQueueSize: 10_000,
        IdlePollInterval: TimeSpan.FromMilliseconds(500));
}
