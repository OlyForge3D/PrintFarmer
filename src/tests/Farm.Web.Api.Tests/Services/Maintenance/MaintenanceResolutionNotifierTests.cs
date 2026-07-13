using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Webhooks;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.Maintenance;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Maintenance;

public sealed class MaintenanceResolutionNotifierTests
{
    [Fact]
    public async Task NotifyCreatedAsync_WebhookThrows_DoesNotPropagate()
    {
        Mock<IClientProxy> client = CreateClientProxy();
        MaintenanceResolutionNotifier notifier = CreateNotifier(
            client,
            out Mock<IWebhookService> webhook);
        webhook
            .Setup(service => service.Enqueue(
                "maintenance.completed",
                It.IsAny<object>()))
            .Throws(new InvalidOperationException("queue unavailable"));

        Func<Task> act = () => notifier.NotifyCreatedAsync(
            Alert(),
            Log(),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        client.Verify(
            proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        webhook.Verify(
            service => service.Enqueue(
                "maintenance.completed",
                It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task NotifyCreatedAsync_SignalRThrows_StillQueuesWebhook()
    {
        Mock<IClientProxy> client = CreateClientProxy();
        client
            .Setup(proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub unavailable"));
        MaintenanceResolutionNotifier notifier = CreateNotifier(
            client,
            out Mock<IWebhookService> webhook);

        Func<Task> act = () => notifier.NotifyCreatedAsync(
            Alert(),
            Log(),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        webhook.Verify(
            service => service.Enqueue(
                "maintenance.completed",
                It.IsAny<object>()),
            Times.Once);
    }

    private static Mock<IClientProxy> CreateClientProxy()
    {
        var client = new Mock<IClientProxy>(MockBehavior.Loose);
        client
            .Setup(proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return client;
    }

    private static MaintenanceResolutionNotifier CreateNotifier(
        Mock<IClientProxy> client,
        out Mock<IWebhookService> webhook)
    {
        var clients = new Mock<IHubClients>(MockBehavior.Strict);
        clients.SetupGet(hubClients => hubClients.All).Returns(client.Object);
        var hub = new Mock<IHubContext<MaintenanceHub>>(MockBehavior.Strict);
        hub.SetupGet(context => context.Clients).Returns(clients.Object);
        webhook = new Mock<IWebhookService>(MockBehavior.Loose);
        return new MaintenanceResolutionNotifier(
            hub.Object,
            webhook.Object,
            NullLogger<MaintenanceResolutionNotifier>.Instance);
    }

    private static MaintenanceAlert Alert() => new()
    {
        Id = Guid.NewGuid(),
        PrinterId = Guid.NewGuid(),
        Status = MaintenanceAlertStatus.Resolved,
        ResolvedAt = DateTime.UtcNow,
        ResolvedBy = "operator"
    };

    private static MaintenanceLog Log() => new()
    {
        Id = Guid.NewGuid(),
        PrinterId = Guid.NewGuid(),
        TaskName = "Lubricate rails",
        PerformedAt = DateTime.UtcNow,
        PerformedBy = "operator"
    };
}
