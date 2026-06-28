using System.Net;
using Farm.Infrastructure.Domain.Notifications;
using Microsoft.Extensions.Logging;
using WebPush;
using DomainPushSubscription = Farm.Infrastructure.Domain.Notifications.PushSubscription;

namespace Farm.Infrastructure.Services.Notifications;

public interface IWebPushNotificationSender
{
    Task<WebPushDispatchResult> SendAsync(DomainPushSubscription subscription, string payload, CancellationToken cancellationToken = default);
}

public sealed class WebPushNotificationSender : IWebPushNotificationSender, IDisposable
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private readonly ILogger<WebPushNotificationSender> _logger;
    private readonly WebPushClient _client;

    public WebPushNotificationSender(ILogger<WebPushNotificationSender> logger)
    {
        _logger = logger;
        _client = new WebPushClient();
    }

    public async Task<WebPushDispatchResult> SendAsync(DomainPushSubscription subscription, string payload, CancellationToken cancellationToken = default)
    {
        string? publicKey = Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
        string? privateKey = Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY");
        string? subject = Environment.GetEnvironmentVariable("VAPID_SUBJECT");

        if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
        {
            _logger.LogWarning("Skipping web push delivery because VAPID keys are not configured");
            return new WebPushDispatchResult(Success: false, SubscriptionExpired: false, Error: "VAPID keys are not configured");
        }

        VapidDetails vapidDetails = new(
            subject: string.IsNullOrWhiteSpace(subject) ? "mailto:noreply@printfarmer.local" : subject,
            publicKey: publicKey,
            privateKey: privateKey);

        try
        {
            var webPushSubscription = new WebPush.PushSubscription(subscription.Endpoint, subscription.P256dh, subscription.Auth);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(SendTimeout);
            await _client.SendNotificationAsync(webPushSubscription, payload, vapidDetails, cancellationToken: timeoutCts.Token);
            return new WebPushDispatchResult(Success: true);
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Removing expired push subscription endpoint {Endpoint}", subscription.Endpoint);
            return new WebPushDispatchResult(Success: false, SubscriptionExpired: true, Error: ex.Message);
        }
        catch (WebPushException ex)
        {
            _logger.LogWarning(ex, "Web push delivery failed for endpoint {Endpoint}", subscription.Endpoint);
            return new WebPushDispatchResult(Success: false, SubscriptionExpired: false, Error: ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected web push delivery failure for endpoint {Endpoint}", subscription.Endpoint);
            return new WebPushDispatchResult(Success: false, SubscriptionExpired: false, Error: ex.Message);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

public sealed record WebPushDispatchResult(bool Success, bool SubscriptionExpired = false, string? Error = null);
