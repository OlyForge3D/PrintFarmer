using Farm.Infrastructure.Services.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Notifications;

public class WebPushNotificationSenderTests
{
    [Fact]
    public async Task SendAsync_WhenVapidKeysNotConfigured_ReturnsFailureWithoutThrowing()
    {
        var sender = new WebPushNotificationSender(
            NullLogger<WebPushNotificationSender>.Instance,
            new VapidOptions());

        var subscription = new Farm.Infrastructure.Domain.Notifications.PushSubscription
        {
            Endpoint = "https://example.com/push/abc",
            P256dh = "p256dh-key",
            Auth = "auth-secret"
        };

        WebPushDispatchResult result = await sender.SendAsync(subscription, "{}");

        result.Success.Should().BeFalse();
        result.SubscriptionExpired.Should().BeFalse();
        result.Error.Should().Be("VAPID keys are not configured");
    }

    [Fact]
    public void VapidOptions_IsConfigured_RequiresBothPublicAndPrivateKey()
    {
        new VapidOptions().IsConfigured.Should().BeFalse();
        new VapidOptions { VapidPublicKey = "pub" }.IsConfigured.Should().BeFalse();
        new VapidOptions { VapidPrivateKey = "priv" }.IsConfigured.Should().BeFalse();
        new VapidOptions { VapidPublicKey = "pub", VapidPrivateKey = "priv" }.IsConfigured.Should().BeTrue();
    }
}
