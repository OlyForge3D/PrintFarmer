using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Notifications.NativePush;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Attention;

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

    [Fact]
    public async Task NotifyChangedAsync_NativePushBlocked_RemainsNonBlockingAndPassesVersion()
    {
        Mock<IHubClients> clients = new();
        Mock<IClientProxy> all = new();
        all.Setup(value => value.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(value => value.All).Returns(all.Object);
        var dispatchEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new Mock<INativePushDispatcher>();
        dispatcher.Setup(value => value.DispatchAsync(
                Payload.ItemId,
                Payload.ChangeKind,
                null,
                Payload.OccurredAt,
                It.IsAny<CancellationToken>()))
            .Returns<string, AttentionChangeKind, Guid?, DateTime?, CancellationToken>(
                async (_, _, _, _, cancellationToken) =>
                {
                    dispatchEntered.TrySetResult();
                    await releaseDispatch.Task.WaitAsync(cancellationToken);
                });
        AttentionBroadcaster broadcaster = CreateBroadcaster(
            clients,
            gateEnabled: true,
            dispatcher.Object);

        Task notification = broadcaster.NotifyChangedAsync(Payload);
        await notification.WaitAsync(TimeSpan.FromSeconds(10));
        await dispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        notification.IsCompletedSuccessfully.Should().BeTrue();

        releaseDispatch.TrySetResult();
        dispatcher.Verify(value => value.DispatchAsync(
                Payload.ItemId,
                Payload.ChangeKind,
                null,
                Payload.OccurredAt,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static AttentionBroadcaster CreateBroadcaster(
        Mock<IHubClients> clients,
        bool gateEnabled,
        INativePushDispatcher? dispatcher = null)
    {
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.IsEnabled(OperatorFeature.Attention)).Returns(gateEnabled);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.Attention, It.IsAny<CancellationToken>())).ReturnsAsync(gateEnabled);

        Mock<IServiceProvider> provider = new();
        provider.Setup(p => p.GetService(typeof(IOperatorFeatureGate))).Returns(gate.Object);
        provider.Setup(p => p.GetService(typeof(INativePushDispatcher))).Returns(dispatcher);

        Mock<IServiceScope> scope = new();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);

        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.SetupGet(l => l.ApplicationStopping).Returns(CancellationToken.None);
        return new AttentionBroadcaster(hub.Object, scopeFactory.Object, lifetime.Object, NullLogger<AttentionBroadcaster>.Instance);
    }
}
