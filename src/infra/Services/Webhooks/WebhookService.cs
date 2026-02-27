using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Data.Interceptors;
using Farm.Infrastructure.Domain.Webhooks;
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
/// Manages webhook delivery with HMAC signing, retries, and failure tracking
/// </summary>
public sealed class WebhookService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ISensitiveDataProtector sensitiveDataProtector,
    ILogger<WebhookService> logger) : BackgroundService, IWebhookService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private const int MaxRetries = 3;
    private const int MaxQueueSize = 10_000;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(5)
    ];

    private static readonly TimeSpan DeliveryLogRetention = TimeSpan.FromDays(90);
    private DateTime _lastCleanup = DateTime.MinValue;

    private readonly ConcurrentQueue<WebhookEvent> _queue = new();

    /// <inheritdoc />
    public void Enqueue(string eventType, object payload)
    {
        if (_queue.Count >= MaxQueueSize)
        {
            logger.LogWarning("Webhook queue full ({MaxSize}), dropping event {EventType}", MaxQueueSize, eventType);
            return;
        }

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _queue.Enqueue(new WebhookEvent(eventType, json, DateTime.UtcNow));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WebhookService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_queue.TryDequeue(out var evt))
            {
                try
                {
                    await ProcessEventAsync(evt, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error processing webhook event {EventType}", evt.EventType);
                }
            }
            else
            {
                // Periodically clean up old delivery logs during idle time
                await CleanupOldDeliveryLogsAsync(stoppingToken);
                await Task.Delay(500, stoppingToken);
            }
        }
    }

    private async Task ProcessEventAsync(WebhookEvent evt, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var subscriptions = await db.WebhookSubscriptions
            .Where(w => w.IsActive)
            .AsNoTracking()
            .ToListAsync(ct);

        var matching = subscriptions
            .Where(w => MatchesEventType(w.EventTypes, evt.EventType))
            .ToList();

        if (matching.Count == 0)
        {
            return;
        }

        logger.LogDebug(
            "Delivering webhook event {EventType} to {Count} subscriptions",
            evt.EventType, matching.Count);

        foreach (WebhookSubscription sub in matching)
        {
            await DeliverAsync(sub, evt, db, ct);
        }
    }

    private async Task DeliverAsync(
        WebhookSubscription subscription,
        WebhookEvent evt,
        AppDbContext db,
        CancellationToken ct)
    {
        var envelope = JsonSerializer.Serialize(
            new
            {
                id = Guid.NewGuid().ToString(),
                @event = evt.EventType,
                timestamp = evt.Timestamp,
                data = JsonSerializer.Deserialize<JsonElement>(evt.PayloadJson)
            }, JsonOptions);

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            var log = new WebhookDeliveryLog
            {
                WebhookSubscriptionId = subscription.Id,
                EventType = evt.EventType,
                Payload = envelope,
                Attempt = attempt
            };

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
                    db.WebhookDeliveryLogs.Add(log);
                    await db.SaveChangesAsync(ct);
                    await UpdateSubscriptionStatusAsync(db, subscription.Id, false, ct);
                    return;
                }

                using var client = httpClientFactory.CreateClient("WebhookDelivery");
                using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url);
                request.Content = new StringContent(envelope, Encoding.UTF8, "application/json");

                // HMAC-SHA256 signature (decrypt secret from encrypted storage)
                if (!string.IsNullOrEmpty(subscription.Secret))
                {
                    string? plaintextSecret = SensitiveDataEncryptionInterceptor.IsAlreadyEncrypted(subscription.Secret)
                        ? sensitiveDataProtector.Unprotect(subscription.Secret)
                        : subscription.Secret;

                    if (!string.IsNullOrEmpty(plaintextSecret))
                    {
                        var hash = HMACSHA256.HashData(
                            Encoding.UTF8.GetBytes(plaintextSecret),
                            Encoding.UTF8.GetBytes(envelope));
                        request.Headers.Add(
                            "X-Webhook-Signature",
                            $"sha256={Convert.ToHexStringLower(hash)}");
                    }
                }

                request.Headers.Add("X-Webhook-Event", evt.EventType);

                var response = await client.SendAsync(request, ct);
                sw.Stop();

                log.StatusCode = (int)response.StatusCode;
                log.DurationMs = sw.ElapsedMilliseconds;
                log.Success = response.IsSuccessStatusCode;

                if (response.IsSuccessStatusCode)
                {
                    // Reset failure counter on success
                    await UpdateSubscriptionStatusAsync(db, subscription.Id, true, ct);
                    db.WebhookDeliveryLogs.Add(log);
                    await db.SaveChangesAsync(ct);
                    return;
                }

                log.ErrorMessage = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                log.DurationMs = sw.ElapsedMilliseconds;
                log.Success = false;
                log.ErrorMessage = ex.Message.Length > 1024
                    ? ex.Message[..1024]
                    : ex.Message;
            }

            db.WebhookDeliveryLogs.Add(log);
            await db.SaveChangesAsync(ct);

            // Retry with backoff (except last attempt)
            if (attempt < MaxRetries)
            {
                await Task.Delay(RetryDelays[attempt - 1], ct);
            }
        }

        // All retries exhausted — increment failure counter
        await UpdateSubscriptionStatusAsync(db, subscription.Id, false, ct);
    }

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
        if (IPAddress.TryParse(uri.Host, out IPAddress? directIp))
        {
            addresses = [directIp];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
        }

        return addresses.Length > 0 && addresses.All(ip => !IsPrivateOrReserved(ip));
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        byte[] bytes = ip.GetAddressBytes();
        return bytes.Length switch
        {
            4 => bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] == 0,
            16 => ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || bytes.All(b => b == 0),
            _ => false,
        };
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
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var cutoff = DateTime.UtcNow - DeliveryLogRetention;
            int deleted = await db.WebhookDeliveryLogs
                .Where(d => d.CreatedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
            {
                logger.LogInformation("Cleaned up {Count} webhook delivery logs older than {Days} days", deleted, DeliveryLogRetention.TotalDays);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up old webhook delivery logs");
        }
    }

    private sealed record WebhookEvent(string EventType, string PayloadJson, DateTime Timestamp);
}
