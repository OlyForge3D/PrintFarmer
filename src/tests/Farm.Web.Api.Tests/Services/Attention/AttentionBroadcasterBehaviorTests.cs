using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Attention;

/// <summary>
/// Behavioural tests for <see cref="AttentionBroadcaster"/>: shared changes fan out to all
/// clients, per-user snooze changes target only that user's connections, and the #725 feature
/// gate suppresses every emission when Attention is disabled. Guards the Dallas realtime
/// contract that one operator's snooze state must never be broadcast to everyone.
/// </summary>
public class AttentionBroadcasterBehaviorTests
{
    private static readonly AttentionChangedPayload Payload =
        new("failure:abc", AttentionChangeKind.Updated, new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task NotifyChangedAsync_Enabled_SendsToAllClients()
    {
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> all = new();
        clients.Setup(c => c.All).Returns(all.Object);
        AttentionBroadcaster broadcaster = CreateBroadcaster(clients, gateEnabled: true);

        await broadcaster.NotifyChangedAsync(Payload);

        all.Verify(
            p => p.SendCoreAsync(IAttentionBroadcaster.EventName, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NotifyUserChangedAsync_Enabled_TargetsOnlyThatUser()
    {
        var userId = Guid.NewGuid();
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> userProxy = new();
        Mock<IClientProxy> all = new();
        clients.Setup(c => c.User(userId.ToString("D"))).Returns(userProxy.Object);
        clients.Setup(c => c.All).Returns(all.Object);
        AttentionBroadcaster broadcaster = CreateBroadcaster(clients, gateEnabled: true);

        await broadcaster.NotifyUserChangedAsync(userId, Payload);

        userProxy.Verify(
            p => p.SendCoreAsync(IAttentionBroadcaster.EventName, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
        clients.Verify(c => c.All, Times.Never);
        all.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NotifyChangedAsync_FeatureDisabled_SendsNothing()
    {
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> all = new();
        clients.Setup(c => c.All).Returns(all.Object);
        AttentionBroadcaster broadcaster = CreateBroadcaster(clients, gateEnabled: false);

        await broadcaster.NotifyChangedAsync(Payload);
        await broadcaster.NotifyUserChangedAsync(Guid.NewGuid(), Payload);

        all.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static AttentionBroadcaster CreateBroadcaster(Mock<IHubClients> clients, bool gateEnabled)
    {
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.IsEnabled(OperatorFeature.Attention)).Returns(gateEnabled);

        Mock<IServiceProvider> provider = new();
        provider.Setup(p => p.GetService(typeof(IOperatorFeatureGate))).Returns(gate.Object);

        Mock<IServiceScope> scope = new();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return new AttentionBroadcaster(hub.Object, scopeFactory.Object, NullLogger<AttentionBroadcaster>.Instance);
    }
}
