using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Notifications.NativePush;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.ServerIdentity;
using Farm.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Notifications.NativePush;

/// <summary>
/// Behavioral coverage for <see cref="NativePushDispatcher"/> covering the fast-path early
/// returns (empty id / Mode.Disabled). Full end-to-end coverage of the triple gate,
/// role-based maintenance filter, and dedupe/rate-limit interactions is exercised through
/// <see cref="AttentionBroadcasterBehaviorTests"/> and the future integration test harness;
/// the tests here lock the observable invariants that the dispatcher never touches its
/// downstream sender or scope factory when it should short-circuit.
/// </summary>
public sealed class NativePushDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_EmptyAttentionItemId_ReturnsImmediately()
    {
        var sender = new Mock<INativePushSender>(MockBehavior.Strict);
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        NativePushDispatcher sut = Build(sender.Object, scopes.Object, NativePushMode.Relay);

        await sut.DispatchAsync("   ", AttentionChangeKind.Created, targetUserId: null);

        sender.VerifyNoOtherCalls();
        scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchAsync_DisabledMode_ReturnsImmediatelyWithoutOpeningScope()
    {
        var sender = new Mock<INativePushSender>(MockBehavior.Strict);
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        NativePushDispatcher sut = Build(sender.Object, scopes.Object, NativePushMode.Disabled);

        await sut.DispatchAsync("att-1", AttentionChangeKind.Created, targetUserId: null);

        sender.VerifyNoOtherCalls();
        scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchAsync_GlobalPushDisabled_DoesNotCallSender()
    {
        // Issue #708 H1-v5 regression: when a persisted preferences row has
        // EnablePushNotifications=false the dispatcher MUST skip the fan-out
        // for every attention native push — even when the per-kind
        // PushOn{Kind} column is still true (preserved legacy value). Prior
        // to the fix the master flag was ignored and preserved per-kind
        // values leaked past the global opt-out. This test drives the
        // dispatcher end-to-end and asserts the sender is never touched.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = false,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var sender = new Mock<INativePushSender>(MockBehavior.Strict);

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        sender.Verify(
            s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_GlobalPushEnabled_ProceedsToSender()
    {
        // Symmetric to the master-gate test: EnablePushNotifications=true with
        // PushOnPrinterOffline=true reaches the sender, proving the new gate
        // does not accidentally block legitimate deliveries.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var sender = new Mock<INativePushSender>();
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        sender.Verify(
            s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task DispatchAsync_ResolvesServerIdentity_PopulatesEnvelopeOriginServerId()
    {
        // Issue #1407: every outgoing envelope must carry the persisted, server-generated
        // origin identity resolved from IServerIdentityService — never a fabricated or
        // empty value.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        Guid expectedServerId = Guid.NewGuid();

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var captured = new List<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((env, _) => captured.Add(env))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        var serverIdentity = new Mock<IServerIdentityService>();
        serverIdentity
            .Setup(s => s.GetOrCreateServerIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedServerId);

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            serverIdentity: serverIdentity.Object);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        captured.Should().ContainSingle();
        captured[0].OriginServerId.Should().Be(expectedServerId);
    }

    [Fact]
    public async Task DispatchAsync_ServerIdentityResolutionThrows_SkipsDeviceWithoutSending()
    {
        // Issue #1407 fail-closed requirement: if the origin server identity cannot be
        // resolved, the dispatcher must isolate the failure to this device (log + skip)
        // rather than sending an envelope with a missing/fabricated origin.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var sender = new Mock<INativePushSender>(MockBehavior.Strict);

        var serverIdentity = new Mock<IServerIdentityService>();
        serverIdentity
            .Setup(s => s.GetOrCreateServerIdAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("server identity unavailable"));

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            serverIdentity: serverIdentity.Object);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchAsync_ServerIdentityResolutionThrowsOnce_IsolatesFailureToSingleDevice()
    {
        // Issue #1407 fail-closed requirement, per-device isolation: a resolution failure
        // for one device's send attempt must not prevent a sibling device (same user) from
        // still receiving its push once identity resolution succeeds.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        Guid expectedServerId = Guid.NewGuid();

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId, deviceCount: 2);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var sender = new Mock<INativePushSender>();
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        var serverIdentity = new Mock<IServerIdentityService>();
        _ = serverIdentity
            .SetupSequence(s => s.GetOrCreateServerIdAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("server identity unavailable"))
            .ReturnsAsync(expectedServerId);

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            serverIdentity: serverIdentity.Object);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        // Exactly one of the two devices was sent to — the other was isolated by the
        // resolution failure, not silently dropped as a whole-dispatch failure.
        sender.Verify(
            s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_ResolvedAfterCreated_EmitsSilentBackgroundPush()
    {
        // Hicks post-merge #1: when a Resolved change arrives after the
        // source has already dropped the live item, the dispatcher must
        // still emit a silent APNs background push so the client can
        // dismiss its cached copy on lock screen / Notification Center.
        // The envelope MUST advertise Background priority with no user-
        // visible alert body, and the sender path exercised end-to-end
        // must send exactly one background envelope.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);

        // Attention service: Created returns the live item; Resolved returns
        // null (source dropped the row). The dispatcher must fall back to
        // the snapshot captured on the Created dispatch.
        var attention = new Mock<IAttentionService>();
        int findCount = 0;
        attention
            .Setup(s => s.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref findCount) == 1 ? item : null);

        var captured = new List<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((env, _) => captured.Add(env))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings
            {
                Mode = NativePushMode.Relay,
                RateLimitPerUser = 1,
                RateLimitWindow = TimeSpan.FromMinutes(5),
            });

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Resolved, targetUserId: null);

        captured.Should().HaveCount(2);
        captured[0].ChangeKind.Should().Be(AttentionChangeKind.Created);
        captured[0].Priority.Should().Be(NativePushPriority.Alert);
        captured[0].Body.Should().Be(item.Title);

        NativePushEnvelope resolved = captured[1];
        resolved.ChangeKind.Should().Be(AttentionChangeKind.Resolved);
        resolved.Priority.Should().Be(NativePushPriority.Background);
        resolved.Body.Should().BeNull();
        resolved.Title.Should().BeNull();
        resolved.Subtitle.Should().BeNull();
        resolved.AttentionItemId.Should().Be(item.Id);
        resolved.AttentionKind.Should().Be(AttentionKind.Offline);
        resolved.PrinterId.Should().Be(item.PrinterId);
    }

    [Fact]
    public async Task DispatchAsync_ResolvedWithoutPriorCreated_NoSenderCall()
    {
        // Hicks post-merge #1: a Resolved arriving with no cached snapshot
        // (dispatcher was cold or Created was suppressed) must NOT emit any
        // push. The SignalR event still invalidates the in-app copy; a
        // synthesised silent push without prior authorization would leak an
        // envelope for a user who never received a Created event.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);

        var attention = new Mock<IAttentionService>();
        attention
            .Setup(s => s.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AttentionItemDto?)null);

        var sender = new Mock<INativePushSender>(MockBehavior.Strict);

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Resolved, targetUserId: null);

        sender.Verify(
            s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_ResolvedSilentPush_HonoursGlobalOptOut()
    {
        // Hicks post-merge #1 authorization safety: even when a snapshot
        // exists from a prior Created dispatch, the Resolved silent push
        // MUST re-evaluate the user's current preferences. Flipping
        // EnablePushNotifications to false between Created and Resolved
        // must suppress the silent envelope — otherwise the snapshot cache
        // becomes an authorization side-channel.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);

        var attention = new Mock<IAttentionService>();
        int findCount = 0;
        attention
            .Setup(s => s.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref findCount) == 1 ? item : null);

        var captured = new List<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((env, _) => captured.Add(env))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        // Created dispatch with no persisted prefs (CLR default opt-in).
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        // User flips global push off between the events.
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = false,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Resolved, targetUserId: null);

        captured.Should().HaveCount(1);
        captured[0].ChangeKind.Should().Be(AttentionChangeKind.Created);
    }

    [Fact]
    public async Task DispatchAsync_ResolvedSnapshots_AreIndependentForEveryEligibleOwner()
    {
        Guid ownerA = Guid.NewGuid();
        Guid ownerB = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        DeviceToken tokenA = MakeToken(ownerA, "owner-a");
        DeviceToken tokenB = MakeToken(ownerB, "owner-b");
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(repository => repository.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([ownerA, ownerB]);
        tokens.Setup(repository => repository.GetActiveByUserAsync(ownerA, It.IsAny<CancellationToken>()))
            .ReturnsAsync([tokenA]);
        tokens.Setup(repository => repository.GetActiveByUserAsync(ownerB, It.IsAny<CancellationToken>()))
            .ReturnsAsync([tokenB]);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var attention = new Mock<IAttentionService>();
        int readsA = 0;
        int readsB = 0;
        attention.Setup(service => service.FindItemAsync(ownerA, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref readsA) == 1 ? item : null);
        attention.Setup(service => service.FindItemAsync(ownerB, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref readsB) == 1 ? item : null);
        var captured = new List<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) => captured.Add(envelope))
            .ReturnsAsync(NativePushDispatchResult.Delivered());
        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Resolved, targetUserId: null);

        captured.Should().HaveCount(4);
        captured.Count(envelope => envelope.ChangeKind == AttentionChangeKind.Created).Should().Be(2);
        captured.Count(envelope => envelope.ChangeKind == AttentionChangeKind.Resolved).Should().Be(2);
        captured.Where(envelope => envelope.ChangeKind == AttentionChangeKind.Resolved)
            .Select(envelope => envelope.Token)
            .Should().BeEquivalentTo(tokenA.Token, tokenB.Token);
    }

    [Fact]
    public async Task DispatchAsync_ResolvedSnapshot_CannotCrossOwnerAuthorizationBoundary()
    {
        Guid authorizedOwner = Guid.NewGuid();
        Guid unauthorizedOwner = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        DeviceToken authorizedToken = MakeToken(authorizedOwner, "authorized");
        DeviceToken unauthorizedToken = MakeToken(unauthorizedOwner, "unauthorized");
        var tokens = new Mock<IDeviceTokenRepository>();
        int ownerReads = 0;
        tokens.Setup(repository => repository.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref ownerReads) == 1
                ? [authorizedOwner, unauthorizedOwner]
                : [unauthorizedOwner, authorizedOwner]);
        tokens.Setup(repository => repository.GetActiveByUserAsync(authorizedOwner, It.IsAny<CancellationToken>()))
            .ReturnsAsync([authorizedToken]);
        tokens.Setup(repository => repository.GetActiveByUserAsync(unauthorizedOwner, It.IsAny<CancellationToken>()))
            .ReturnsAsync([unauthorizedToken]);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var attention = new Mock<IAttentionService>();
        int authorizedReads = 0;
        attention.Setup(service => service.FindItemAsync(
                authorizedOwner,
                item.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref authorizedReads) == 1 ? item : null);
        attention.Setup(service => service.FindItemAsync(
                unauthorizedOwner,
                item.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((AttentionItemDto?)null);
        var captured = new List<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) => captured.Add(envelope))
            .ReturnsAsync(NativePushDispatchResult.Delivered());
        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Resolved, targetUserId: null);

        captured.Should().HaveCount(2);
        captured.Should().OnlyContain(envelope => envelope.Token == authorizedToken.Token);
        captured.Select(envelope => envelope.ChangeKind)
            .Should().Equal(AttentionChangeKind.Created, AttentionChangeKind.Resolved);
    }

    [Fact]
    public async Task DispatchAsync_AllTransientDeliveryFailureThenResolved_SkipsDismissalAsBenignNoOp()
    {
        // #756: every attempt for this recipient's alert generation exhausts
        // as Transient — the device never actually received the Created
        // alert. A later Resolved must treat the dismissal as a benign no-op
        // rather than send a silent push clearing something never shown.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(BuildPushPreferences(userId));
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        DeviceToken deviceToken = MakeToken(userId, "always-transient-device");
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(repository => repository.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([userId]);
        tokens.Setup(repository => repository.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([deviceToken]);

        var attention = new Mock<IAttentionService>();
        int findCount = 0;
        attention.Setup(service => service.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref findCount) == 1 ? item : null);

        var captured = new List<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) => captured.Add(envelope))
            .ReturnsAsync(NativePushDispatchResult.Transient("timeout"));

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                MaxAttempts = 3,
            },
            new ImmediateTimeProvider(new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)));

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Resolved, targetUserId: null);

        captured.Should().HaveCount(
            3,
            "MaxAttempts exhausts all 3 transient retries for the Created alert and no Resolved dismissal is ever sent");
        captured.Should().OnlyContain(envelope => envelope.ChangeKind == AttentionChangeKind.Created);
        tokens.Verify(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        tokens.Verify(repository => repository.RecordFailureAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        tokens.Verify(repository => repository.InvalidateAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_MixedSuccessfulAndNeverDeliveredRecipients_ResolvedOnlyDismissesDeliveredRecipient()
    {
        // #756: per-recipient partial success must be preserved. ownerDelivered
        // got a successful device delivery and is owed a dismissal;
        // ownerNeverDelivered exhausts every attempt as Transient across its
        // own device and must not receive a synthetic dismissal for an alert
        // it never received. One owner's outcome must not affect the other's.
        Guid ownerDelivered = Guid.NewGuid();
        Guid ownerNeverDelivered = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.AddRange(
            BuildPushPreferences(ownerDelivered),
            BuildPushPreferences(ownerNeverDelivered));
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        DeviceToken deliveredToken = MakeToken(ownerDelivered, "delivered-device");
        DeviceToken neverDeliveredToken = MakeToken(ownerNeverDelivered, "never-delivered-device");
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(repository => repository.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([ownerDelivered, ownerNeverDelivered]);
        tokens.Setup(repository => repository.GetActiveByUserAsync(ownerDelivered, It.IsAny<CancellationToken>()))
            .ReturnsAsync([deliveredToken]);
        tokens.Setup(repository => repository.GetActiveByUserAsync(ownerNeverDelivered, It.IsAny<CancellationToken>()))
            .ReturnsAsync([neverDeliveredToken]);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var attention = new Mock<IAttentionService>();
        int deliveredReads = 0;
        int neverDeliveredReads = 0;
        attention.Setup(service => service.FindItemAsync(ownerDelivered, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref deliveredReads) == 1 ? item : null);
        attention.Setup(service => service.FindItemAsync(ownerNeverDelivered, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref neverDeliveredReads) == 1 ? item : null);

        var captured = new List<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((envelope, _) =>
            {
                captured.Add(envelope);
                return Task.FromResult(envelope.Token == neverDeliveredToken.Token
                    ? NativePushDispatchResult.Transient("timeout")
                    : NativePushDispatchResult.Delivered());
            });

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                MaxAttempts = 2,
            },
            new ImmediateTimeProvider(new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)));

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);
        captured.Clear();

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Resolved, targetUserId: null);

        captured.Should().ContainSingle(
            "only the recipient with at least one successful delivery is owed a dismissal");
        NativePushEnvelope resolved = captured.Single();
        resolved.ChangeKind.Should().Be(AttentionChangeKind.Resolved);
        resolved.Token.Should().Be(deliveredToken.Token);
    }

    [Fact]
    public async Task DispatchAsync_CreatedDeliveredThenUpdatedNeverDelivered_ResolvedStillDismisses()
    {
        // #756 follow-up: a later Updated generation for the same recipient may
        // fail every retry, but it must inherit the earlier Created delivery
        // state so Resolved still clears the alert the client already saw.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(BuildPushPreferences(userId));
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        DeviceToken deviceToken = MakeToken(userId, "created-delivered-then-updated-transient");
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(repository => repository.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([userId]);
        tokens.Setup(repository => repository.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([deviceToken]);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var attention = new Mock<IAttentionService>();
        int findCount = 0;
        attention.Setup(service => service.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref findCount) <= 2 ? item : null);

        var captured = new List<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((envelope, _) =>
            {
                captured.Add(envelope);
                return Task.FromResult(envelope.ChangeKind == AttentionChangeKind.Updated
                    ? NativePushDispatchResult.Transient("timeout")
                    : NativePushDispatchResult.Delivered());
            });

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                MaxAttempts = 2,
            },
            new ImmediateTimeProvider(new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)));

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);
        captured.Clear();

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Updated, targetUserId: null);
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Resolved, targetUserId: null);

        captured.Select(envelope => envelope.ChangeKind).Should().Equal(
            AttentionChangeKind.Updated,
            AttentionChangeKind.Updated,
            AttentionChangeKind.Resolved);
        NativePushEnvelope resolved = captured.Last();
        resolved.ChangeKind.Should().Be(AttentionChangeKind.Resolved);
        resolved.Token.Should().Be(deviceToken.Token);
    }

    [Fact]
    public async Task DispatchAsync_MultipleGenerationsAllTransient_ResolvedRemainsBenignNoOp()
    {
        // #756 invariant on the #755 lifecycle-owned architecture (behavioral
        // replacement for the removed reflection/CAS harness).
        //
        // The concern the removed harness targeted: a snapshot displacement
        // path must not spuriously carry a "delivered" bit forward. Prove it
        // through the public dispatch seam alone:
        //
        //   1. Created fails every retry (never delivered). Snapshot #1 must
        //      remain "not delivered".
        //   2. Updated at a strictly newer version displaces snapshot #1 via
        //      the lifecycle's under-lock swap. If any leak existed, the new
        //      snapshot could inherit a stale delivery bit. Updated also
        //      fails every retry (never delivered).
        //   3. Resolved observes the current snapshot. The alert generation
        //      never reached the device, so the dismissal is a benign no-op:
        //      the sender must never receive a Resolved envelope and no
        //      token attribution must occur.
        //
        // If the displacement leaked a delivery bit, the Resolved would emit
        // a silent dismissal for an alert the device never saw — the exact
        // regression #756 prevents. This test asserts the observable outcome
        // (no Resolved send) rather than the internal field shape.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(BuildPushPreferences(userId));
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        DeviceToken deviceToken = MakeToken(userId, "multi-generation-transient");
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(repository => repository.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([userId]);
        tokens.Setup(repository => repository.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([deviceToken]);

        var attention = new Mock<IAttentionService>();
        int findCount = 0;
        attention.Setup(service => service.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref findCount) <= 2 ? item : null);

        var captured = new List<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) => captured.Add(envelope))
            .ReturnsAsync(NativePushDispatchResult.Transient("timeout"));

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                MaxAttempts = 2,
            },
            new ImmediateTimeProvider(new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc)));

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Updated, targetUserId: null);
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Resolved, targetUserId: null);

        captured.Select(envelope => envelope.ChangeKind).Should().Equal(
            new[]
            {
                AttentionChangeKind.Created,
                AttentionChangeKind.Created,
                AttentionChangeKind.Updated,
                AttentionChangeKind.Updated,
            },
            "MaxAttempts exhausts Created and Updated as transient with no delivery ever recorded; the Resolved dismissal must be suppressed as a benign no-op because the client never received either generation of the alert");
        captured.Should().NotContain(envelope => envelope.ChangeKind == AttentionChangeKind.Resolved);
        tokens.Verify(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        tokens.Verify(repository => repository.RecordFailureAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        tokens.Verify(repository => repository.InvalidateAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_PerKindPushOffButMasterOn_DoesNotCallSender()
    {
        // Bishop v6 hardening: EnablePushNotifications=true crossed with
        // PushOnPrinterFailure=false must still short-circuit before the
        // sender is touched. This closes the missing symmetric coverage for
        // the per-kind gate (Hicks v5 H1 covers only the master gate).
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Failure);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterFailure = false,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var sender = new Mock<INativePushSender>(MockBehavior.Strict);

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        sender.Verify(
            s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_FirstTransientAttemptDisablesPersistedKillSwitch_StopsRetryAndFanOut()
    {
        Guid firstOwner = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid laterOwner = Guid.Parse("00000000-0000-0000-0000-000000000002");
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"native-push-kill-switch-{Guid.NewGuid():N}.db");
        string connectionString =
            $"Data Source={databasePath};Pooling=False;Default Timeout=5";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using (AppDbContext seed = new(options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Users.AddRange(
                    BuildUser(firstOwner, "kill-switch-first"),
                    BuildUser(laterOwner, "kill-switch-later"));
                seed.NotificationPreferences.AddRange(
                    BuildPushPreferences(firstOwner),
                    BuildPushPreferences(laterOwner));
                seed.AppSettingsEntities.Add(new AppSettingsEntity
                {
                    Key = OperatorFeatureSettings.SectionName,
                    SettingsJson = JsonSerializer.Serialize(new OperatorFeatureSettings
                    {
                        NativePushEnabled = true,
                    }),
                    UpdatedAt = DateTime.UtcNow,
                });
                await seed.SaveChangesAsync();

                var registrations = new EfDeviceTokenRepository(seed);
                _ = await registrations.UpsertAsync(
                    firstOwner,
                    "kill-switch-first-a",
                    new string('a', 64),
                    "ios",
                    "production",
                    "com.example.app");
                _ = await registrations.UpsertAsync(
                    firstOwner,
                    "kill-switch-first-b",
                    new string('b', 64),
                    "ios",
                    "production",
                    "com.example.app");
                _ = await registrations.UpsertAsync(
                    laterOwner,
                    "kill-switch-later",
                    new string('c', 64),
                    "ios",
                    "production",
                    "com.example.app");
            }

            var attention = new Mock<IAttentionService>();
            attention.Setup(service => service.FindItemAsync(
                    It.IsAny<Guid>(),
                    item.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(item);

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(connectionString));
            services.AddScoped<IAppSettingsRepository, EfAppSettingsRepository>();
            services.AddScoped<IOperatorFeatureGate, OperatorFeatureGate>();
            services.AddScoped<IDeviceTokenRepository, EfDeviceTokenRepository>();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton<ILogger<OperatorFeatureGate>>(
                NullLogger<OperatorFeatureGate>.Instance);
            services.AddSingleton<IAttentionService>(attention.Object);
            await using ServiceProvider provider = services.BuildServiceProvider();

            int persistedDisableCount = 0;
            var sent = new ConcurrentQueue<NativePushEnvelope>();
            var sender = new Mock<INativePushSender>();
            sender.SetupGet(value => value.ModeName).Returns("direct");
            sender.Setup(value => value.SendAsync(
                    It.IsAny<NativePushEnvelope>(),
                    It.IsAny<CancellationToken>()))
                .Returns<NativePushEnvelope, CancellationToken>(async (envelope, cancellationToken) =>
                {
                    sent.Enqueue(envelope);
                    if (sent.Count == 1)
                    {
                        await using AppDbContext disable = new(options);
                        int updated = await disable.AppSettingsEntities
                            .Where(row => row.Key == OperatorFeatureSettings.SectionName)
                            .ExecuteUpdateAsync(
                                setters => setters
                                    .SetProperty(
                                        row => row.SettingsJson,
                                        JsonSerializer.Serialize(new OperatorFeatureSettings
                                        {
                                            NativePushEnabled = false,
                                        }))
                                    .SetProperty(row => row.UpdatedAt, DateTime.UtcNow),
                                cancellationToken);
                        Interlocked.Exchange(ref persistedDisableCount, updated);
                    }

                    return NativePushDispatchResult.Transient("timeout");
                });

            using var sut = new NativePushDispatcher(
                provider.GetRequiredService<IServiceScopeFactory>(),
                AsTransportAwareForTests(sender.Object),
                new StaticOptionsMonitor(new NativePushSettings
                {
                    Mode = NativePushMode.Direct,
                    MaxAttempts = 3,
                }),
                new NativePushMetrics(),
                NullLogger<NativePushDispatcher>.Instance);

            await sut.DispatchAsync(
                    item.Id,
                    AttentionChangeKind.Created,
                    targetUserId: null)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Volatile.Read(ref persistedDisableCount).Should().Be(1);
            sent.Should().ContainSingle(
                "the first transient attempt commits the persisted kill-switch before retry two");
            sent.Single().Token.Should().BeOneOf(new string('a', 64), new string('b', 64));
            attention.Verify(service => service.FindItemAsync(
                    laterOwner,
                    item.Id,
                    It.IsAny<CancellationToken>()),
                Times.Never,
                "the owner-level gate must stop fan-out before resolving the later owner");

            await using AppDbContext verify = new(options);
            DeviceToken[] persistedRegistrations = await verify.DeviceTokens.AsNoTracking().ToArrayAsync();
            persistedRegistrations.Should().OnlyContain(registration =>
                registration.ConsecutiveFailureCount == 0
                && registration.LastFailureAt == null
                && registration.IsActive);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Theory]
    [InlineData(NativePushMode.Direct)]
    [InlineData(NativePushMode.Relay)]
    public async Task DispatchAsync_InvalidationCompletesAfterPersistedDisable_DiscardsResultAndStopsFanOut(
        NativePushMode mode)
    {
        await AssertPostSendDisableDiscardsResultAsync(
            mode,
            NativePushDispatchResult.Invalidated("BadDeviceToken"));
    }

    [Theory]
    [InlineData(NativePushMode.Direct)]
    [InlineData(NativePushMode.Relay)]
    public async Task DispatchAsync_TokenFailureCompletesAfterPersistedDisable_DiscardsResultAndStopsFanOut(
        NativePushMode mode)
    {
        await AssertPostSendDisableDiscardsResultAsync(
            mode,
            NativePushDispatchResult.TokenFailure("provider-token-failure"));
    }

    [Fact]
    public async Task DispatchAsync_ConcurrentDeleteMakesStaleOutcomeNoOpAndContinuesWithFreshContexts()
    {
        Guid ownerA = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid ownerB = Guid.Parse("00000000-0000-0000-0000-000000000002");
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        DeviceToken tokenA = MakeToken(ownerA, "install-a1");
        DeviceToken tokenB = MakeToken(ownerA, "install-a2");
        DeviceToken laterOwnerToken = MakeToken(ownerB, "install-b1");
        tokenA.Id = Guid.Parse("00000000-0000-0000-0000-000000000101");
        tokenB.Id = Guid.Parse("00000000-0000-0000-0000-000000000102");
        laterOwnerToken.Id = Guid.Parse("00000000-0000-0000-0000-000000000201");
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"native-push-persistence-{Guid.NewGuid():N}.db");
        string connectionString =
            $"Data Source={databasePath};Pooling=False;Default Timeout=5";
        DbContextOptions<AppDbContext> plainOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var interceptor = new TokenOutcomeDeleteRaceInterceptor(tokenA.Id);

        try
        {
            await using (AppDbContext seed = new(plainOptions))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Users.AddRange(
                    BuildUser(ownerA, "owner-a"),
                    BuildUser(ownerB, "owner-b"));
                seed.NotificationPreferences.AddRange(
                    BuildPushPreferences(ownerA),
                    BuildPushPreferences(ownerB));
                seed.DeviceTokens.AddRange(tokenA, tokenB, laterOwnerToken);
                await seed.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(options => options
                .UseSqlite(connectionString)
                .AddInterceptors(interceptor));
            services.AddScoped<IDeviceTokenRepository, EfDeviceTokenRepository>();
            services.AddSingleton<IOperatorFeatureGate>(BuildGate(enabled: true).Object);
            var attention = new Mock<IAttentionService>();
            attention.Setup(service => service.FindItemAsync(
                    It.IsAny<Guid>(),
                    item.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(item);
            services.AddSingleton<IAttentionService>(attention.Object);
            await using ServiceProvider provider = services.BuildServiceProvider();

            var sentTokenIds = new List<Guid>();
            var sender = new Mock<INativePushSender>();
            sender.SetupGet(value => value.ModeName).Returns("direct");
            sender.Setup(value => value.SendAsync(
                    It.IsAny<NativePushEnvelope>(),
                    It.IsAny<CancellationToken>()))
                .Callback<NativePushEnvelope, CancellationToken>((envelope, _) =>
                    sentTokenIds.Add(Guid.Parse(envelope.DeviceTokenId)))
                .ReturnsAsync(NativePushDispatchResult.Delivered());
            var logger = new RecordingDispatcherLogger();
            using var sut = new NativePushDispatcher(
                provider.GetRequiredService<IServiceScopeFactory>(),
                AsTransportAwareForTests(sender.Object),
                new StaticOptionsMonitor(new NativePushSettings { Mode = NativePushMode.Direct }),
                new NativePushMetrics(),
                logger);

            Task dispatch = sut.DispatchAsync(
                item.Id,
                AttentionChangeKind.Created,
                targetUserId: null);
            await interceptor.TokenAUpdateReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                await using AppDbContext concurrentDelete = new(plainOptions);
                DeviceToken doomed = await concurrentDelete.DeviceTokens
                    .SingleAsync(token => token.Id == tokenA.Id);
                concurrentDelete.DeviceTokens.Remove(doomed);
                await concurrentDelete.SaveChangesAsync();
                interceptor.DeleteCommitted.TrySetResult();
            }
            catch (Exception exception)
            {
                interceptor.DeleteCommitted.TrySetException(exception);
                throw;
            }

            await dispatch.WaitAsync(TimeSpan.FromSeconds(10));

            sentTokenIds.Should().Equal(tokenA.Id, tokenB.Id, laterOwnerToken.Id);
            interceptor.PersistenceContextIds.Should().HaveCount(3);
            interceptor.PersistenceContextIds.Should().OnlyHaveUniqueItems(
                "every token outcome must use an independent scoped AppDbContext");
            logger.Exceptions.Should().BeEmpty(
                "a conditionally stale outcome is an expected zero-row no-op, not a persistence failure");

            await using AppDbContext verify = new(plainOptions);
            DeviceToken[] remaining = await verify.DeviceTokens.AsNoTracking().ToArrayAsync();
            remaining.Select(token => token.Id).Should().BeEquivalentTo(new[] { tokenB.Id, laterOwnerToken.Id });
            remaining.Select(token => token.LastUsedAt).Should().OnlyContain(lastUsedAt => lastUsedAt.HasValue);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task DispatchAsync_SenderFailureForOneOwner_ContinuesOtherOwners()
    {
        // Vasquez v6 B1 regression: if the sender itself throws unexpectedly
        // for the first owner's token, the second owner must still receive
        // their fan-out. Cancellation is not signalled — only a random
        // exception (simulated as InvalidOperationException) inside the
        // per-token send scope.
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        foreach (Guid u in new[] { ownerA, ownerB })
        {
            db.NotificationPreferences.Add(new NotificationPreferences
            {
                Id = Guid.NewGuid().ToString(),
                UserId = u,
                EnablePushNotifications = true,
                PushOnPrinterOffline = true,
                AttentionPushCategoryPreferencesJson = null,
            });
        }

        await db.SaveChangesAsync();

        var attention = new Mock<IAttentionService>();
        attention
            .Setup(s => s.FindItemAsync(It.IsAny<Guid>(), item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        DeviceToken ownerAToken = MakeToken(ownerA, "install-a");
        DeviceToken ownerBToken = MakeToken(ownerB, "install-b");

        var tokens = new Mock<IDeviceTokenRepository>();
        tokens
            .Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { ownerA, ownerB });
        tokens
            .Setup(r => r.GetActiveByUserAsync(ownerA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken> { ownerAToken });
        tokens
            .Setup(r => r.GetActiveByUserAsync(ownerB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken> { ownerBToken });
        tokens
            .Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);

        int totalSendAttempts = 0;
        int ownerBSendAttempts = 0;
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((env, ct) =>
            {
                System.Threading.Interlocked.Increment(ref totalSendAttempts);
                if (string.Equals(env.Token, ownerAToken.Token, StringComparison.Ordinal))
                {
                    // Sender throws for owner A only. The dispatcher must
                    // catch this at the per-device scope and continue on to
                    // owner B, which must still be able to deliver.
                    throw new InvalidOperationException("simulated sender failure for owner A");
                }

                System.Threading.Interlocked.Increment(ref ownerBSendAttempts);
                return Task.FromResult(NativePushDispatchResult.Delivered());
            });

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        // Owner A's send was attempted (and threw). SendWithRetriesAsync
        // catches the sender throw and re-shapes it into a transient result,
        // so the outer per-device scope may still complete normally. What
        // this test is really pinning is that owner B's send was ALSO
        // attempted — which proves owner A's failure did not abort the loop.
        totalSendAttempts.Should().BeGreaterThanOrEqualTo(2, "owner A and owner B both need a send attempt");
        ownerBSendAttempts.Should().BeGreaterThanOrEqualTo(1, "owner A's failure must not abort owner B");
    }

    [Fact]
    public async Task DispatchAsync_TransientTimeout_RetriesExactlyAndContinuesDevicesAndOwners()
    {
        Guid ownerA = Guid.NewGuid();
        Guid ownerB = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.AddRange(
            BuildPushPreferences(ownerA),
            BuildPushPreferences(ownerB));
        await db.SaveChangesAsync();

        DeviceToken timeoutToken = MakeToken(ownerA, "timeout-device");
        DeviceToken laterDevice = MakeToken(ownerA, "later-device");
        DeviceToken laterOwner = MakeToken(ownerB, "later-owner");
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(repository => repository.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([ownerA, ownerB]);
        tokens.Setup(repository => repository.GetActiveByUserAsync(ownerA, It.IsAny<CancellationToken>()))
            .ReturnsAsync([timeoutToken, laterDevice]);
        tokens.Setup(repository => repository.GetActiveByUserAsync(ownerB, It.IsAny<CancellationToken>()))
            .ReturnsAsync([laterOwner]);
        var persistedSuccesses = new ConcurrentBag<Guid>();
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, long, DateTime, CancellationToken>((id, _, _, _) => persistedSuccesses.Add(id))
            .Returns(Task.CompletedTask);

        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                It.IsAny<Guid>(),
                item.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        var attempts = new ConcurrentQueue<Guid>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((envelope, _) =>
            {
                Guid tokenId = Guid.Parse(envelope.DeviceTokenId);
                attempts.Enqueue(tokenId);
                return Task.FromResult(tokenId == timeoutToken.Id
                    ? NativePushDispatchResult.Transient("timeout")
                    : NativePushDispatchResult.Delivered());
            });
        DateTime nowUtc = new(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc);
        var clock = new ImmediateTimeProvider(nowUtc);
        NativePushDispatcher sut = BuildWithScope(
            sender,
            BuildGate(enabled: true).Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                MaxAttempts = 3,
            },
            clock);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        attempts.Should().Equal(
            timeoutToken.Id,
            timeoutToken.Id,
            timeoutToken.Id,
            laterDevice.Id,
            laterOwner.Id);
        persistedSuccesses.Should().BeEquivalentTo(new[] { laterDevice.Id, laterOwner.Id });
    }

    [Theory]
    [InlineData(AttentionSeverity.Info, 5)]
    [InlineData(AttentionSeverity.Warning, 30)]
    [InlineData(AttentionSeverity.Critical, 30)]
    public async Task DispatchAsync_LongDeadline_CapsExpirationBySeverity(
        AttentionSeverity severity,
        int expectedMinutes)
    {
        Guid userId = Guid.NewGuid();
        DateTime nowUtc = new(2026, 7, 14, 11, 0, 0, DateTimeKind.Utc);
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline) with
        {
            Severity = severity,
            DeadlineAt = nowUtc.AddDays(2),
        };
        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
        });
        await db.SaveChangesAsync();
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);
        NativePushEnvelope? captured = null;
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) => captured = envelope)
            .ReturnsAsync(NativePushDispatchResult.Delivered());
        var clock = new ImmediateTimeProvider(nowUtc);
        NativePushDispatcher sut = BuildWithScope(
            sender,
            BuildGate(enabled: true).Object,
            tokens.Object,
            attention.Object,
            db,
            timeProvider: clock);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: userId);

        captured.Should().NotBeNull();
        captured!.ExpiresAtUtc.Should().Be(nowUtc.AddMinutes(expectedMinutes));
    }

    [Fact]
    public async Task DispatchAsync_DeadlineEarlierThanSeverityCap_UsesDeadline()
    {
        Guid userId = Guid.NewGuid();
        DateTime nowUtc = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
        DateTime deadline = nowUtc.AddMinutes(2);
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline) with
        {
            Severity = AttentionSeverity.Critical,
            DeadlineAt = deadline,
        };
        await using AppDbContext db = BuildDbContext();
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);
        NativePushEnvelope? captured = null;
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) => captured = envelope)
            .ReturnsAsync(NativePushDispatchResult.Delivered());
        NativePushDispatcher sut = BuildWithScope(
            sender,
            BuildGate(enabled: true).Object,
            tokens.Object,
            attention.Object,
            db,
            timeProvider: new ImmediateTimeProvider(nowUtc));

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: userId);

        captured!.ExpiresAtUtc.Should().Be(deadline);
    }

    [Fact]
    public async Task DispatchAsync_RegistrationRefreshDuringSuccessfulSend_StaleSuccessDoesNotMutateReplacement()
    {
        await AssertRegistrationRefreshRejectsStaleOutcomeAsync(NativePushDispatchResult.Delivered());
    }

    [Fact]
    public async Task DispatchAsync_RegistrationRefreshDuringTransientFailure_PreservesReplacement()
    {
        await AssertRegistrationRefreshRejectsStaleOutcomeAsync(
            NativePushDispatchResult.Transient("provider-timeout"));
    }

    [Fact]
    public async Task DispatchAsync_RegistrationRefreshDuringTokenFailure_StaleFailureDoesNotMutateReplacement()
    {
        await AssertRegistrationRefreshRejectsStaleOutcomeAsync(
            NativePushDispatchResult.TokenFailure("device-token-failure"));
    }

    [Fact]
    public async Task DispatchAsync_RegistrationRefreshDuringNonTokenTerminalFailure_PreservesReplacement()
    {
        await AssertRegistrationRefreshRejectsStaleOutcomeAsync(
            NativePushDispatchResult.Terminal("provider-configuration-failure"));
    }

    [Fact]
    public async Task DispatchAsync_RegistrationRefreshDuringInvalidation_StaleInvalidationDoesNotDeleteReplacement()
    {
        await AssertRegistrationRefreshRejectsStaleOutcomeAsync(
            NativePushDispatchResult.Invalidated("BadDeviceToken"));
    }

    [Fact]
    public async Task DispatchAsync_SandboxBadDeviceToken_DeletesOnlyExactSandboxRegistration()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        string sharedProviderToken = new string('a', 64);
        DeviceToken sandbox = MakeToken(userId, "sandbox-install");
        sandbox.Token = sharedProviderToken;
        sandbox.Environment = "development";
        sandbox.AppBundleId = "com.example.sandbox";
        DeviceToken production = MakeToken(userId, "production-install");
        production.Token = sharedProviderToken;
        production.Environment = "production";
        production.AppBundleId = "com.example.production";
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"native-push-invalidation-{Guid.NewGuid():N}.db");
        string connectionString =
            $"Data Source={databasePath};Pooling=False;Default Timeout=5";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using (AppDbContext seed = new(options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Users.Add(BuildUser(userId, "invalidation-owner"));
                seed.NotificationPreferences.Add(BuildPushPreferences(userId));
                seed.DeviceTokens.AddRange(sandbox, production);
                await seed.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(connectionString));
            services.AddScoped<IDeviceTokenRepository, EfDeviceTokenRepository>();
            services.AddSingleton<IOperatorFeatureGate>(BuildGate(enabled: true).Object);
            var attention = new Mock<IAttentionService>();
            attention.Setup(service => service.FindItemAsync(
                    userId,
                    item.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(item);
            services.AddSingleton<IAttentionService>(attention.Object);
            await using ServiceProvider provider = services.BuildServiceProvider();

            var sender = new Mock<INativePushSender>();
            sender.SetupGet(value => value.ModeName).Returns("direct");
            sender.Setup(value => value.SendAsync(
                    It.IsAny<NativePushEnvelope>(),
                    It.IsAny<CancellationToken>()))
                .Returns<NativePushEnvelope, CancellationToken>((envelope, _) =>
                    Task.FromResult(envelope.Environment == "development"
                        ? NativePushDispatchResult.Invalidated("BadDeviceToken")
                        : NativePushDispatchResult.Delivered()));
            using var sut = new NativePushDispatcher(
                provider.GetRequiredService<IServiceScopeFactory>(),
                AsTransportAwareForTests(sender.Object),
                new StaticOptionsMonitor(new NativePushSettings { Mode = NativePushMode.Direct }),
                new NativePushMetrics(),
                NullLogger<NativePushDispatcher>.Instance);

            await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: userId);

            await using AppDbContext verify = new(options);
            DeviceToken remaining = await verify.DeviceTokens.AsNoTracking().SingleAsync();
            remaining.Id.Should().Be(production.Id);
            remaining.Token.Should().Be(sharedProviderToken);
            remaining.Environment.Should().Be("production");
            remaining.LastUsedAt.Should().NotBeNull();
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task DispatchAsync_SenderThrowsOceWithInternalToken_PropagatesWhenCallerTokenIsNone()
    {
        // An unexpected OperationCanceledException raised from an arbitrary
        // sender's internal or linked token must still propagate when the
        // caller passed CancellationToken.None. Concrete HTTP senders convert
        // only their own HttpClient.Timeout into a typed transient result;
        // the dispatcher must not guess that every unrelated OCE is a timeout.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens
            .Setup(r => r.RecordFailureAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        // Sender throws OCE with an internal token that has already been
        // cancelled, modeling an unexpected linked cancellation rather than
        // the concrete senders' classified HttpClient.Timeout path.
        using var innerCts = new CancellationTokenSource();
        innerCts.Cancel();

        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((_, _) =>
                Task.FromException<NativePushDispatchResult>(new OperationCanceledException(innerCts.Token)));

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        // Caller cancellation token is None; the unexpected inner OCE still
        // propagates instead of being swallowed by fan-out isolation.
        Func<Task> act = () => sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null, cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DispatchAsync_PersistenceThrowsOceWithInternalToken_PropagatesWhenCallerTokenIsNone()
    {
        // Hicks #1 regression companion: the same rule for the persistence
        // catch. A cancellation raised inside RecordSuccessAsync — for
        // example when the ambient DbContext link chain trips a linked
        // token — must not be swallowed by the per-device Exception
        // isolator. Sender path completes normally with Delivered; only the
        // persistence step throws OCE with an internal token, and caller
        // passes CancellationToken.None.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        using var innerCts = new CancellationTokenSource();
        innerCts.Cancel();
        tokens
            .Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, long, DateTime, CancellationToken>((_, _, _, _) => Task.FromException(new OperationCanceledException(innerCts.Token)));
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        Func<Task> act = () => sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null, cancellationToken: CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DispatchAsync_JwtSignFailedTerminal_DoesNotDeactivateToken()
    {
        // Hicks H5-v5-final regression: JWT sign failure is a deployment
        // problem (wrong .p8 / TeamId / KeyId), not a bad device token. The
        // dispatcher must NOT tick the token failure counter — otherwise the
        // 5th outage would deactivate every registered token and require
        // every client to re-register once the config was corrected.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        int recordFailureCount = 0;
        tokens
            .Setup(r => r.RecordFailureAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => System.Threading.Interlocked.Increment(ref recordFailureCount))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NativePushDispatchResult.Terminal("jwt_sign_failed"));

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        recordFailureCount.Should().Be(0, "jwt_sign_failed is deployment-scoped and MUST NOT tick the token failure counter");
    }

    [Theory]
    [InlineData("TopicDisallowed")]
    [InlineData("PayloadTooLarge")]
    [InlineData("BadTopic")]
    [InlineData("PayloadEmpty")]
    [InlineData("BadMessageId")]
    public async Task DispatchAsync_ConfigOrPayloadTerminal_DoesNotDeactivateToken(string reason)
    {
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        int recordFailureCount = 0;
        tokens
            .Setup(r => r.RecordFailureAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => System.Threading.Interlocked.Increment(ref recordFailureCount))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NativePushDispatchResult.Terminal(reason));

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        recordFailureCount.Should().Be(0, $"terminal reason '{reason}' is not token-attributable and must not deactivate");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task DispatchAsync_TerminalFailure_OnlyTypedTokenAttributionChangesHealth(
        bool tokenAttributable,
        int expectedFailureWrites)
    {
        // A reason string is untrusted provider/relay data and cannot poison a
        // registration. Only the sender's typed attribution permits a health write.
        var userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        int recordFailureCount = 0;
        tokens
            .Setup(r => r.RecordFailureAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => System.Threading.Interlocked.Increment(ref recordFailureCount))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tokenAttributable
                ? NativePushDispatchResult.TokenFailure("provider-token-failure")
                : NativePushDispatchResult.Terminal("BadDeviceToken"));

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        recordFailureCount.Should().Be(expectedFailureWrites);
    }

    [Fact]
    public async Task DispatchAsync_RateLimit_IsChargedOnceAcrossDevicesAndScopedPerPrinter()
    {
        // Hicks H2-v5-final regression: rate limit is (userId, printerId,
        // kind)-scoped and charged BEFORE per-device fan-out. A three-device
        // user must not exhaust the bucket three times faster than a
        // one-device user.
        var userId = Guid.NewGuid();
        AttentionItemDto item1 = BuildAttentionItem(AttentionKind.Offline);
        AttentionItemDto item2 = BuildAttentionItem(AttentionKind.Offline) with { PrinterId = item1.PrinterId };
        AttentionItemDto otherPrinter = BuildAttentionItem(AttentionKind.Offline);

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        DeviceToken t1 = MakeToken(userId, "install-1");
        DeviceToken t2 = MakeToken(userId, "install-2");
        DeviceToken t3 = MakeToken(userId, "install-3");
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens
            .Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { userId });
        tokens
            .Setup(r => r.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken> { t1, t2, t3 });
        tokens
            .Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var attention = new Mock<IAttentionService>();
        attention.Setup(s => s.FindItemAsync(userId, item1.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item1);
        attention.Setup(s => s.FindItemAsync(userId, item2.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item2);
        attention.Setup(s => s.FindItemAsync(userId, otherPrinter.Id, It.IsAny<CancellationToken>())).ReturnsAsync(otherPrinter);

        int sendCount = 0;
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                System.Threading.Interlocked.Increment(ref sendCount);
                return Task.FromResult(NativePushDispatchResult.Delivered());
            });

        var services = new ServiceCollection();
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton<IAttentionService>(attention.Object);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var monitor = new StaticOptionsMonitor(new NativePushSettings
        {
            Mode = NativePushMode.Relay,
            RateLimitPerUser = 1,
            RateLimitWindow = TimeSpan.FromMinutes(5),
        });
        var sut = new NativePushDispatcher(
            scopeFactory,
            AsTransportAwareForTests(sender.Object),
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);

        await sut.DispatchAsync(item1.Id, AttentionChangeKind.Created, targetUserId: null);
        int sendCountAfterFirstEnvelope = sendCount;

        await sut.DispatchAsync(item2.Id, AttentionChangeKind.Created, targetUserId: null);
        int sendCountAfterSameBucket = sendCount;
        await sut.DispatchAsync(otherPrinter.Id, AttentionChangeKind.Created, targetUserId: null);

        sendCountAfterFirstEnvelope.Should().Be(3, "rate bucket is consumed once per envelope; all three devices must be reached");
        sendCountAfterSameBucket.Should().Be(3, "second envelope for the same (user, printer, kind) is rate-limited");
        sendCount.Should().Be(6, "the same kind on another printer has an independent bucket");
    }

    [Fact]
    public async Task DispatchAsync_UnrelatedSnapshots_StartSynchronousSenderWorkConcurrently()
    {
        Guid firstUserId = Guid.NewGuid();
        Guid secondUserId = Guid.NewGuid();
        AttentionItemDto firstItem = BuildAttentionItem(AttentionKind.Offline);
        AttentionItemDto secondItem = BuildAttentionItem(AttentionKind.Offline);

        string databaseName = Guid.NewGuid().ToString();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        await using (var db = new AppDbContext(options))
        {
            db.NotificationPreferences.AddRange(
            BuildPushPreferences(firstUserId),
            BuildPushPreferences(secondUserId));
            await db.SaveChangesAsync();
        }

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(r => r.GetActiveByUserAsync(firstUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeToken(firstUserId, "first-installation")]);
        tokens.Setup(r => r.GetActiveByUserAsync(secondUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeToken(secondUserId, "second-installation")]);
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var attention = new Mock<IAttentionService>();
        attention.Setup(s => s.FindItemAsync(firstUserId, firstItem.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstItem);
        attention.Setup(s => s.FindItemAsync(secondUserId, secondItem.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(secondItem);

        var firstStartupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStartupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender.Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((envelope, cancellationToken) =>
            {
                TaskCompletionSource entered = envelope.AttentionItemId == firstItem.Id
                    ? firstStartupEntered
                    : secondStartupEntered;
                TaskCompletionSource release = envelope.AttentionItemId == firstItem.Id
                    ? releaseFirstStartup
                    : releaseSecondStartup;

                entered.TrySetResult();
                release.Task.Wait(cancellationToken);
                return Task.FromResult(NativePushDispatchResult.Delivered());
            });

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder => builder.UseInMemoryDatabase(databaseName));
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton<IAttentionService>(attention.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var sut = new NativePushDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            AsTransportAwareForTests(sender.Object),
            new StaticOptionsMonitor(new NativePushSettings { Mode = NativePushMode.Direct }),
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);

        Task firstDispatch = Task.Factory.StartNew(
            () => sut.DispatchAsync(firstItem.Id, AttentionChangeKind.Created, firstUserId),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        Task? secondDispatch = null;
        try
        {
            await firstStartupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            secondDispatch = Task.Factory.StartNew(
                () => sut.DispatchAsync(secondItem.Id, AttentionChangeKind.Created, secondUserId),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
            await secondStartupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseFirstStartup.TrySetResult();
            releaseSecondStartup.TrySetResult();
        }

        secondDispatch.Should().NotBeNull();
        await Task.WhenAll(firstDispatch, secondDispatch!).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DispatchAsync_RateLimit_ScopedPerKindNotPerUser()
    {
        // Hicks H2-v5-final regression: a noisy kind must not silence
        // unrelated critical alerts (different kind) for the same user.
        var userId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        AttentionItemDto offline = BuildAttentionItem(AttentionKind.Offline) with { PrinterId = printerId };
        AttentionItemDto failure = BuildAttentionItem(AttentionKind.Failure) with { PrinterId = printerId };

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            PushOnPrinterFailure = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens
            .Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var attention = new Mock<IAttentionService>();
        attention.Setup(s => s.FindItemAsync(userId, offline.Id, It.IsAny<CancellationToken>())).ReturnsAsync(offline);
        attention.Setup(s => s.FindItemAsync(userId, failure.Id, It.IsAny<CancellationToken>())).ReturnsAsync(failure);

        int sendCount = 0;
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                System.Threading.Interlocked.Increment(ref sendCount);
                return Task.FromResult(NativePushDispatchResult.Delivered());
            });

        var services = new ServiceCollection();
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton<IAttentionService>(attention.Object);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var monitor = new StaticOptionsMonitor(new NativePushSettings
        {
            Mode = NativePushMode.Relay,
            RateLimitPerUser = 1,
            RateLimitWindow = TimeSpan.FromMinutes(5),
        });
        var sut = new NativePushDispatcher(
            scopeFactory,
            AsTransportAwareForTests(sender.Object),
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);

        await sut.DispatchAsync(offline.Id, AttentionChangeKind.Created, targetUserId: null);
        await sut.DispatchAsync(offline.Id, AttentionChangeKind.Updated, targetUserId: null);
        int sendsAfterOfflineFlood = sendCount;

        await sut.DispatchAsync(failure.Id, AttentionChangeKind.Created, targetUserId: null);

        sendsAfterOfflineFlood.Should().Be(1, "second offline envelope hits its own kind-scoped rate bucket");
        sendCount.Should().Be(2, "a different kind (failure) has its own rate bucket and must not be silenced");
    }

    [Fact]
    public async Task DispatchAsync_DeduplicatedEvent_DoesNotConsumeRateLimitCapacity()
    {
        var userId = Guid.NewGuid();
        AttentionItemDto first = BuildAttentionItem(AttentionKind.Offline);
        AttentionItemDto third = BuildAttentionItem(AttentionKind.Offline) with
        {
            PrinterId = first.PrinterId,
        };

        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens
            .Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var attention = new Mock<IAttentionService>();
        attention
            .Setup(service => service.FindItemAsync(
                userId,
                first.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(first);
        attention
            .Setup(service => service.FindItemAsync(
                userId,
                third.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(third);

        int sendCount = 0;
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender
            .Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref sendCount);
                return Task.FromResult(NativePushDispatchResult.Delivered());
            });

        var services = new ServiceCollection();
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton<IAttentionService>(attention.Object);
        services.AddSingleton(db);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var monitor = new StaticOptionsMonitor(new NativePushSettings
        {
            Mode = NativePushMode.Relay,
            DedupeWindow = TimeSpan.FromMinutes(5),
            RateLimitPerUser = 2,
            RateLimitWindow = TimeSpan.FromMinutes(5),
        });
        using var sut = new NativePushDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            AsTransportAwareForTests(sender.Object),
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);

        await sut.DispatchAsync(first.Id, AttentionChangeKind.Created, targetUserId: null);
        await sut.DispatchAsync(first.Id, AttentionChangeKind.Created, targetUserId: null);
        await sut.DispatchAsync(third.Id, AttentionChangeKind.Created, targetUserId: null);

        sendCount.Should().Be(
            2,
            "the duplicate must be discarded before rate capacity is consumed, leaving room for the distinct third event");
    }

    private static async Task AssertRegistrationRefreshRejectsStaleOutcomeAsync(
        NativePushDispatchResult staleOutcome)
    {
        Guid userId = Guid.NewGuid();
        const string installationId = "refreshing-installation";
        string tokenA = new('a', 64);
        string tokenB = new('b', 64);
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"native-push-registration-refresh-{Guid.NewGuid():N}.db");
        string connectionString =
            $"Data Source={databasePath};Pooling=False;Default Timeout=5";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            DeviceToken registrationA;
            await using (AppDbContext seed = new(options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Users.Add(BuildUser(userId, "refresh-owner"));
                seed.NotificationPreferences.Add(BuildPushPreferences(userId));
                await seed.SaveChangesAsync();
                registrationA = await new EfDeviceTokenRepository(seed).UpsertAsync(
                    userId,
                    installationId,
                    tokenA,
                    "ios",
                    "production",
                    "com.example.topic-a");
            }

            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(connectionString));
            services.AddScoped<IDeviceTokenRepository, EfDeviceTokenRepository>();
            services.AddSingleton<IOperatorFeatureGate>(BuildGate(enabled: true).Object);
            var attention = new Mock<IAttentionService>();
            attention.Setup(service => service.FindItemAsync(
                    userId,
                    item.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(item);
            services.AddSingleton<IAttentionService>(attention.Object);
            await using ServiceProvider provider = services.BuildServiceProvider();

            var sendStarted = new TaskCompletionSource<NativePushEnvelope>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSend = new TaskCompletionSource<NativePushDispatchResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var sender = new Mock<INativePushSender>();
            sender.SetupGet(value => value.ModeName).Returns("direct");
            sender.Setup(value => value.SendAsync(
                    It.IsAny<NativePushEnvelope>(),
                    It.IsAny<CancellationToken>()))
                .Returns<NativePushEnvelope, CancellationToken>(async (envelope, cancellationToken) =>
                {
                    sendStarted.TrySetResult(envelope);
                    return await releaseSend.Task.WaitAsync(cancellationToken);
                });
            using var sut = new NativePushDispatcher(
                provider.GetRequiredService<IServiceScopeFactory>(),
                AsTransportAwareForTests(sender.Object),
                new StaticOptionsMonitor(new NativePushSettings
                {
                    Mode = NativePushMode.Direct,
                    MaxAttempts = 1,
                    FailureDeactivationThreshold = 1,
                }),
                new NativePushMetrics(),
                NullLogger<NativePushDispatcher>.Instance);

            Task dispatch = sut.DispatchAsync(
                item.Id,
                AttentionChangeKind.Created,
                targetUserId: userId);
            try
            {
                NativePushEnvelope dispatched = await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                dispatched.Token.Should().Be(tokenA);
                dispatched.Environment.Should().Be("production");
                dispatched.AppBundleId.Should().Be("com.example.topic-a");

                DeviceToken replacement;
                await using (AppDbContext refresh = new(options))
                {
                    replacement = await new EfDeviceTokenRepository(refresh).UpsertAsync(
                        userId,
                        installationId,
                        tokenB,
                        "ios",
                        "development",
                        "com.example.topic-b");
                }

                replacement.Id.Should().Be(registrationA.Id);
                replacement.RegistrationVersion.Should().Be(registrationA.RegistrationVersion + 1);

                DeviceToken replacementBaseline;
                await using (AppDbContext baseline = new(options))
                {
                    replacementBaseline = await baseline.DeviceTokens.AsNoTracking().SingleAsync();
                }

                releaseSend.TrySetResult(staleOutcome);
                await dispatch.WaitAsync(TimeSpan.FromSeconds(10));

                await using AppDbContext verify = new(options);
                DeviceToken persisted = await verify.DeviceTokens.AsNoTracking().SingleAsync();
                persisted.Id.Should().Be(replacementBaseline.Id);
                persisted.UserId.Should().Be(replacementBaseline.UserId);
                persisted.InstallationId.Should().Be(replacementBaseline.InstallationId);
                persisted.RegistrationVersion.Should().Be(replacementBaseline.RegistrationVersion);
                persisted.Token.Should().Be(tokenB);
                persisted.Platform.Should().Be(replacementBaseline.Platform);
                persisted.Environment.Should().Be("development");
                persisted.AppBundleId.Should().Be("com.example.topic-b");
                persisted.IsActive.Should().BeTrue();
                persisted.ConsecutiveFailureCount.Should().Be(0);
                persisted.LastFailureAt.Should().BeNull();
                persisted.LastUsedAt.Should().Be(replacementBaseline.LastUsedAt);
            }
            finally
            {
                releaseSend.TrySetResult(NativePushDispatchResult.Transient("test-cleanup"));
                await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task DispatchAsync_CreatedBlockedBeforeSnapshot_ResolvedPublishesFenceAndVetoesAlert()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        var createdLookupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreatedLookup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int lookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                item.Id,
                It.IsAny<CancellationToken>()))
            .Returns<Guid, string, CancellationToken>(async (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref lookupCount) == 1)
                {
                    createdLookupEntered.TrySetResult();
                    await releaseCreatedLookup.Task.WaitAsync(cancellationToken);
                    return item;
                }

                return null;
            });
        var sent = new ConcurrentQueue<AttentionChangeKind>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) =>
                sent.Enqueue(envelope.ChangeKind))
            .ReturnsAsync(NativePushDispatchResult.Delivered());
        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db);
        DateTime createdAt = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);

        Task created = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await createdLookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Task resolved = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            userId,
            occurredAtUtc: createdAt.AddSeconds(1));

        sent.Should().BeEmpty();
        await resolved.WaitAsync(TimeSpan.FromSeconds(30));

        releaseCreatedLookup.TrySetResult();
        await created.WaitAsync(TimeSpan.FromSeconds(30));

        sent.Should().BeEmpty(
            "the resolution fence is published while Created is outside the narrow lane in its item lookup, so Created cannot install a snapshot or start transport afterward");
    }

    [Fact]
    public async Task DispatchAsync_CreatedBlockedAfterSnapshot_ResolvedWaitsForAlertCompletion()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        var attention = new Mock<IAttentionService>();
        int lookupCount = 0;
        attention.Setup(service => service.FindItemAsync(
                userId,
                item.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref lookupCount) == 1 ? item : null);
        var firstSendEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstSend = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<AttentionChangeKind>();
        int sendCount = 0;
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>(async (envelope, cancellationToken) =>
            {
                sent.Enqueue(envelope.ChangeKind);
                if (Interlocked.Increment(ref sendCount) == 1)
                {
                    firstSendEntered.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(cancellationToken);
                }

                return NativePushDispatchResult.Delivered();
            });
        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db);
        DateTime createdAt = new(2026, 7, 14, 13, 0, 0, DateTimeKind.Utc);

        Task created = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await firstSendEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task resolved = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            userId,
            occurredAtUtc: createdAt.AddSeconds(1));

        sent.Should().Equal(AttentionChangeKind.Created);
        resolved.IsCompleted.Should().BeFalse();

        releaseFirstSend.TrySetResult();
        await Task.WhenAll(created, resolved).WaitAsync(TimeSpan.FromSeconds(10));

        sent.Should().Equal(AttentionChangeKind.Created, AttentionChangeKind.Resolved);
    }

    [Fact]
    public async Task DispatchAsync_TargetedUpdatedRetryAfterGlobalResolved_DoesNotResendStaleAlert()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        int lookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                item.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref lookupCount) == 1 ? item : null);
        var sent = new ConcurrentQueue<AttentionChangeKind>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((envelope, _) =>
            {
                sent.Enqueue(envelope.ChangeKind);
                return Task.FromResult(envelope.ChangeKind == AttentionChangeKind.Updated
                    ? NativePushDispatchResult.Transient("timeout")
                    : NativePushDispatchResult.Delivered());
            });
        DateTime updatedAt = new(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc);
        var clock = new ControlledRetryTimeProvider(updatedAt);
        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                MaxAttempts = 2,
            },
            clock);

        Task updated = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Updated,
            userId,
            occurredAtUtc: updatedAt);
        try
        {
            await clock.RetryDelayStarted.WaitAsync(TimeSpan.FromSeconds(10));

            await sut.DispatchAsync(
                    item.Id,
                    AttentionChangeKind.Resolved,
                    targetUserId: null,
                    occurredAtUtc: updatedAt.AddSeconds(1))
                .WaitAsync(TimeSpan.FromSeconds(10));

            // #756: the only attempt so far for this generation returned
            // Transient("timeout") and the pending retry that could still
            // succeed is fenced by this same resolution consuming the
            // snapshot below — this recipient has zero successful
            // deliveries for this generation, so the dismissal is a benign
            // no-op and must NOT be sent.
            sent.Should().Equal(AttentionChangeKind.Updated);

            clock.ReleaseRetry();
            await updated.WaitAsync(TimeSpan.FromSeconds(10));

            sent.Should().Equal(
                new[] { AttentionChangeKind.Updated },
                "the consumed snapshot makes the pending targeted retry obsolete and the never-delivered generation suppresses the dismissal");
            sender.Verify(value => value.SendAsync(
                    It.IsAny<NativePushEnvelope>(),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(1));
            tokens.Verify(repository => repository.RecordSuccessAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<long>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            tokens.Verify(repository => repository.RecordFailureAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<long>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            tokens.Verify(repository => repository.InvalidateAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<long>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            clock.ReleaseRetry();
            await updated.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DispatchAsync_TargetedCaptureAfterGlobalResolution_DoesNotSendStaleAlert()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        var staleLookupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleLookup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int lookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                item.Id,
                It.IsAny<CancellationToken>()))
            .Returns<Guid, string, CancellationToken>(async (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref lookupCount) == 1)
                {
                    staleLookupEntered.TrySetResult();
                    await releaseStaleLookup.Task.WaitAsync(cancellationToken);
                    return item;
                }

                return null;
            });
        var sender = new Mock<INativePushSender>(MockBehavior.Strict);
        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db);
        DateTime createdAt = new(2026, 7, 14, 16, 0, 0, DateTimeKind.Utc);

        Task staleCreated = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await staleLookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await sut.DispatchAsync(
                item.Id,
                AttentionChangeKind.Resolved,
                targetUserId: null,
                occurredAtUtc: createdAt.AddSeconds(1))
            .WaitAsync(TimeSpan.FromSeconds(10));

        releaseStaleLookup.TrySetResult();
        await staleCreated.WaitAsync(TimeSpan.FromSeconds(10));

        sender.Verify(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_OlderTargetedCaptureAfterNewerGlobalCapture_PreservesNewerSnapshot()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto olderItem = BuildAttentionItem(AttentionKind.Offline);
        AttentionItemDto newerItem = olderItem with
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "Newer printer",
        };
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        var olderLookupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOlderLookup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int lookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                olderItem.Id,
                It.IsAny<CancellationToken>()))
            .Returns<Guid, string, CancellationToken>(async (_, _, cancellationToken) =>
            {
                int call = Interlocked.Increment(ref lookupCount);
                if (call == 1)
                {
                    olderLookupEntered.TrySetResult();
                    await releaseOlderLookup.Task.WaitAsync(cancellationToken);
                    return olderItem;
                }

                return call == 2 ? newerItem : null;
            });
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) =>
                sent.Enqueue(envelope))
            .ReturnsAsync(NativePushDispatchResult.Delivered());
        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db);
        DateTime olderAt = new(2026, 7, 14, 17, 0, 0, DateTimeKind.Utc);

        Task olderCreated = sut.DispatchAsync(
            olderItem.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: olderAt);
        await olderLookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await sut.DispatchAsync(
                olderItem.Id,
                AttentionChangeKind.Updated,
                targetUserId: null,
                occurredAtUtc: olderAt.AddSeconds(1))
            .WaitAsync(TimeSpan.FromSeconds(10));

        releaseOlderLookup.TrySetResult();
        await olderCreated.WaitAsync(TimeSpan.FromSeconds(10));
        await sut.DispatchAsync(
                olderItem.Id,
                AttentionChangeKind.Resolved,
                targetUserId: null,
                occurredAtUtc: olderAt.AddSeconds(2))
            .WaitAsync(TimeSpan.FromSeconds(10));

        sent.Select(envelope => envelope.ChangeKind).Should().Equal(
            AttentionChangeKind.Updated,
            AttentionChangeKind.Resolved);
        sent.Should().OnlyContain(envelope => envelope.PrinterId == newerItem.PrinterId);
    }

    [Fact]
    public async Task DispatchAsync_NewOccurrenceAfterResolution_SendsNewAlertAndDismissal()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto firstOccurrence = BuildAttentionItem(AttentionKind.Offline);
        AttentionItemDto secondOccurrence = firstOccurrence with
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "Replacement printer",
        };
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        int lookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                firstOccurrence.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref lookupCount) switch
            {
                1 => firstOccurrence,
                2 => null,
                _ => secondOccurrence,
            });
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) =>
                sent.Enqueue(envelope))
            .ReturnsAsync(NativePushDispatchResult.Delivered());
        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db);
        DateTime firstAt = new(2026, 7, 14, 18, 0, 0, DateTimeKind.Utc);

        await sut.DispatchAsync(
            firstOccurrence.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: firstAt);
        await sut.DispatchAsync(
            firstOccurrence.Id,
            AttentionChangeKind.Resolved,
            userId,
            occurredAtUtc: firstAt.AddSeconds(1));
        await sut.DispatchAsync(
            firstOccurrence.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: firstAt.AddSeconds(2));
        await sut.DispatchAsync(
            firstOccurrence.Id,
            AttentionChangeKind.Resolved,
            userId,
            occurredAtUtc: firstAt.AddSeconds(3));

        sent.Select(envelope => envelope.ChangeKind).Should().Equal(
            AttentionChangeKind.Created,
            AttentionChangeKind.Resolved,
            AttentionChangeKind.Created,
            AttentionChangeKind.Resolved);
        sent.Skip(2).Should().OnlyContain(envelope => envelope.PrinterId == secondOccurrence.PrinterId);
    }

    [Fact]
    public async Task DispatchAsync_CancelledResolutionBeforeTransport_PreservesSnapshotForNewerResolution()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        var cancelledLookupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int lookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                item.Id,
                It.IsAny<CancellationToken>()))
            .Returns<Guid, string, CancellationToken>(async (_, _, cancellationToken) =>
            {
                int call = Interlocked.Increment(ref lookupCount);
                if (call == 1)
                {
                    return item;
                }

                if (call == 2)
                {
                    cancelledLookupEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return null;
            });
        var sent = new ConcurrentQueue<AttentionChangeKind>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) =>
                sent.Enqueue(envelope.ChangeKind))
            .ReturnsAsync(NativePushDispatchResult.Delivered());
        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db);
        DateTime createdAt = new(2026, 7, 14, 19, 0, 0, DateTimeKind.Utc);

        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        using var cancellation = new CancellationTokenSource();
        Task cancelledResolution = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            userId,
            occurredAtUtc: createdAt.AddSeconds(1),
            cancellationToken: cancellation.Token);
        await cancelledLookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        Func<Task> awaitCancelledResolution = () => cancelledResolution;
        await awaitCancelledResolution.Should().ThrowAsync<OperationCanceledException>();

        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            userId,
            occurredAtUtc: createdAt.AddSeconds(1));

        sent.Should().Equal(AttentionChangeKind.Created, AttentionChangeKind.Resolved);
    }

    [Fact]
    public async Task DispatchAsync_EqualVersionSameLaneRetryAfterPreCaptureFailure_SendsAlert()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        int lookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                item.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (Interlocked.Increment(ref lookupCount) == 1)
                {
                    throw new InvalidOperationException("transient lookup failure");
                }

                return item;
            });
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("direct");
        sender.Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(NativePushDispatchResult.Delivered());
        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db);
        DateTime occurredAt = new(2026, 7, 14, 20, 0, 0, DateTimeKind.Utc);

        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: occurredAt);
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: occurredAt);

        sender.Verify(value => value.SendAsync(
                It.Is<NativePushEnvelope>(envelope =>
                    envelope.ChangeKind == AttentionChangeKind.Created),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_StaleCreatedAfterResolved_IsRejected()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);
        var sender = new Mock<INativePushSender>(MockBehavior.Strict);
        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db);
        DateTime resolvedAt = new(2026, 7, 14, 14, 0, 0, DateTimeKind.Utc);

        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            userId,
            occurredAtUtc: resolvedAt);
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: resolvedAt.AddSeconds(-1));

        sender.Verify(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        attention.Verify(service => service.FindItemAsync(
                userId,
                item.Id,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_GlobalResolvedWithTokenlessLifecycleOwner_FencesInFlightTargetedRetry()
    {
        // #755 remediation blocker 1: a global resolution must establish the
        // lifecycle tombstone for every recipient with an active lifecycle,
        // not only recipients that currently hold device tokens. Otherwise a
        // temporarily tokenless recipient's in-flight targeted lane can resume
        // after re-registration and send a stale alert past the resolution.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);

        DeviceToken activeToken = MakeToken(userId, "install-a");
        bool userHasTokens = true;
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<IReadOnlyList<Guid>>(
                userHasTokens ? new List<Guid> { userId } : new List<Guid>()));
        tokens.Setup(r => r.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<IReadOnlyList<DeviceToken>>(
                userHasTokens ? new List<DeviceToken> { activeToken } : new List<DeviceToken>()));
        tokens.Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        int lookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(s => s.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref lookupCount) == 1 ? item : null);

        var sent = new ConcurrentQueue<AttentionChangeKind>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender.Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((envelope, _) =>
            {
                sent.Enqueue(envelope.ChangeKind);
                return Task.FromResult(envelope.ChangeKind == AttentionChangeKind.Updated
                    ? NativePushDispatchResult.Transient("timeout")
                    : NativePushDispatchResult.Delivered());
            });

        DateTime updatedAt = new(2026, 7, 14, 22, 0, 0, DateTimeKind.Utc);
        var clock = new ControlledRetryTimeProvider(updatedAt);
        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                MaxAttempts = 2,
            },
            clock);

        Task updated = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Updated,
            userId,
            occurredAtUtc: updatedAt);
        try
        {
            await clock.RetryDelayStarted.WaitAsync(TimeSpan.FromSeconds(10));

            // Simulate the user losing all device tokens between the transient
            // response and the retry — the recipient's lifecycle is still live
            // in _attentionLifecycles even though GetActiveTokenOwnersAsync
            // now returns an empty list.
            userHasTokens = false;

            await sut.DispatchAsync(
                    item.Id,
                    AttentionChangeKind.Resolved,
                    targetUserId: null,
                    occurredAtUtc: updatedAt.AddSeconds(1))
                .WaitAsync(TimeSpan.FromSeconds(10));

            sent.Should().Equal(
                new[] { AttentionChangeKind.Updated },
                "resolution has no tokens to dispatch a silent dismissal to, but must still tombstone the lifecycle");

            clock.ReleaseRetry();
            await updated.WaitAsync(TimeSpan.FromSeconds(10));

            sent.Should().Equal(
                new[] { AttentionChangeKind.Updated },
                "the tokenless recipient's lifecycle was tombstoned by the global resolution; the pending targeted retry must not resend the stale alert");
            tokens.Verify(repository => repository.RecordSuccessAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<long>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
            tokens.Verify(repository => repository.RecordFailureAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<long>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            clock.ReleaseRetry();
            await updated.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DispatchAsync_ResolvedDedupeResetIsAtomicWithObservation_LegitimateNewerCreatedEmits()
    {
        // #755 remediation blocker 2: ResetActiveLifecycleDedupe must run
        // under the lifecycle sync lock at TryObserve time for Resolved.
        // If it ran later (outside the lock after an async lookup) an in-flight
        // newer Created would race the reset — either the reset erased newer
        // dedupe state, or the newer Created was suppressed by an old dedupe
        // entry that resolution had not yet cleared. This test proves the
        // second failure mode is fixed: a newer Created that arrives while
        // Resolved is blocked on FindItemAsync still emits.
        Guid userId = Guid.NewGuid();
        AttentionItemDto initial = BuildAttentionItem(AttentionKind.Offline);
        AttentionItemDto recurrence = initial with
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "Recurrent printer",
        };
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var resolvedLookupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResolvedLookup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int lookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                initial.Id,
                It.IsAny<CancellationToken>()))
            .Returns<Guid, string, CancellationToken>(async (_, _, ct) =>
            {
                int call = Interlocked.Increment(ref lookupCount);
                if (call == 1)
                {
                    return initial;
                }

                if (call == 2)
                {
                    resolvedLookupEntered.TrySetResult();
                    await releaseResolvedLookup.Task.WaitAsync(ct);
                    return null;
                }

                return recurrence;
            });

        var sent = new ConcurrentQueue<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender.Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((env, _) => sent.Enqueue(env))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings
            {
                Mode = NativePushMode.Relay,
                DedupeWindow = TimeSpan.FromMinutes(5),
                RateLimitPerUser = 5,
                RateLimitWindow = TimeSpan.FromMinutes(5),
            });
        DateTime t1 = new(2026, 7, 14, 23, 0, 0, DateTimeKind.Utc);

        // Step 1: Created v1 delivers; a dedupe entry for (user, item, Created)
        // is committed and would suppress a subsequent Created for the same key.
        await sut.DispatchAsync(initial.Id, AttentionChangeKind.Created, userId, occurredAtUtc: t1);

        // Step 2: Global Resolved v2 is dispatched (target=null) but blocks
        // inside FindItemAsync. With the fix, ResetActiveLifecycleDedupe ran
        // synchronously under the lifecycle sync lock at TryObserve time
        // (before FindItemAsync), so the Created dedupe entry is already
        // cleared. With the bug, the reset ran later — after the async lookup
        // — leaving the stale dedupe active during the window a newer
        // targeted Created might arrive. Global (target=null) uses a
        // different AttentionDispatchLane than the targeted (target=userId)
        // Created v3 dispatched next, so step 3 is not serialised behind step
        // 2's blocked lookup.
        Task resolved = sut.DispatchAsync(initial.Id, AttentionChangeKind.Resolved, targetUserId: null, occurredAtUtc: t1.AddSeconds(1));
        await resolvedLookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Step 3: While Resolved is blocked, a newer Created arrives.
        // With the fix, the Created dedupe was cleared under the lock, so this
        // legitimate recurrence emits. With the bug, it is suppressed by the
        // stale v1 dedupe entry that Resolved has not yet cleared.
        await sut.DispatchAsync(initial.Id, AttentionChangeKind.Created, userId, occurredAtUtc: t1.AddSeconds(2));

        // Step 4: Release Resolved. Its own TryBeginSend now sees a superseded
        // lifecycle version (v3 > v2) and returns Stale, so it does not send.
        releaseResolvedLookup.TrySetResult();
        await resolved.WaitAsync(TimeSpan.FromSeconds(10));

        NativePushEnvelope[] captured = sent.ToArray();
        captured.Select(e => e.ChangeKind).Should().Equal(
            AttentionChangeKind.Created,
            AttentionChangeKind.Created);
        captured[0].PrinterId.Should().Be(initial.PrinterId);
        captured[1].PrinterId.Should().Be(recurrence.PrinterId);
    }

    [Fact]
    public async Task DispatchAsync_SyncSenderExceptionBeforeTransport_AllowsSameVersionRetry()
    {
        // #755 remediation blocker 3: when the sender throws synchronously
        // before transport truly starts, the lifecycle must NOT commit
        // _latestCommitted / _snapshot / dedupe / rate. Otherwise an
        // exact-version retry via a subsequent DispatchAsync would find the
        // lifecycle already committed at the same version and short-circuit
        // with Stale, poisoning legitimate replay of the same event.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        int attemptCount = 0;
        var sender = new DelegateTransportSender(async (_, transportStart, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attemptCount) == 1)
            {
                throw new InvalidOperationException("simulated synchronous sender failure before transport");
            }

            (await transportStart.TryStartAsync(cancellationToken)).IsPermitted.Should().BeTrue();
            return NativePushDispatchResult.Delivered();
        });

        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        DateTime occurredAt = new(2026, 7, 14, 20, 30, 0, DateTimeKind.Utc);

        // First dispatch: the sender throws synchronously. With the fix, the
        // lifecycle rolls back dedupe + rate reservations and leaves
        // _latestCommitted false. With the bug, the pre-startSend commit
        // fences same-version retries.
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);

        // Second dispatch with the same version. With the fix this proceeds
        // through TryObserveLifecycle (not stale) and delivers. With the bug
        // it short-circuits at TryObserveLifecycle because _latestCommitted is
        // true, and the sender is never called a second time.
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);

        attemptCount.Should().Be(
            2,
            "the exact-version retry must proceed after the first attempt's transport never started");
        tokens.Verify(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(AttentionChangeKind.Created)]
    [InlineData(AttentionChangeKind.Updated)]
    public async Task DispatchAsync_TargetedChangeRecurrenceAfterGlobalResolution_EmitsAsNewOccurrence(
        AttentionChangeKind recurrenceKind)
    {
        // #755 remediation blocker 4: #708 public contracts permit targeted
        // Created and Updated. Both must obey cross-lane ordering and honour a
        // legitimate recurrence after a global resolution — even though the
        // targeted lane is a different AttentionDispatchKey, the shared
        // per-(recipient, item) lifecycle must still admit the newer version.
        Guid userId = Guid.NewGuid();
        AttentionItemDto initial = BuildAttentionItem(AttentionKind.Offline);
        AttentionItemDto recurrence = initial with
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "Recurrent printer",
        };
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        int lookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                initial.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int call = Interlocked.Increment(ref lookupCount);
                if (call == 1)
                {
                    return initial;
                }

                if (call == 2)
                {
                    return null;
                }

                return recurrence;
            });

        var sent = new ConcurrentQueue<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender.Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((env, _) => sent.Enqueue(env))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings
            {
                Mode = NativePushMode.Relay,
                DedupeWindow = TimeSpan.FromMinutes(5),
                RateLimitPerUser = 5,
                RateLimitWindow = TimeSpan.FromMinutes(5),
            });
        DateTime t1 = new(2026, 7, 14, 21, 0, 0, DateTimeKind.Utc);

        // 1. Global Created v1 — active alert dispatched.
        await sut.DispatchAsync(
            initial.Id,
            AttentionChangeKind.Created,
            targetUserId: null,
            occurredAtUtc: t1);

        // 2. Global Resolved v2 — silent dismissal dispatched.
        await sut.DispatchAsync(
            initial.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: t1.AddSeconds(1));

        // 3. Targeted recurrence (Created OR Updated) v3 — must emit as a new
        // occurrence. Cross-lane ordering + dedupe reset atomicity must not
        // suppress the legitimate recurrence.
        await sut.DispatchAsync(
            initial.Id,
            recurrenceKind,
            targetUserId: userId,
            occurredAtUtc: t1.AddSeconds(2));

        NativePushEnvelope[] captured = sent.ToArray();
        captured.Select(e => e.ChangeKind).Should().Equal(
            AttentionChangeKind.Created,
            AttentionChangeKind.Resolved,
            recurrenceKind);
        captured[0].PrinterId.Should().Be(initial.PrinterId);
        captured[2].PrinterId.Should().Be(recurrence.PrinterId);
        captured[2].AttentionItemId.Should().Be(initial.Id);
    }

    [Fact]
    public async Task DispatchAsync_MultiDeviceSyncSenderExceptionForEveryDeviceOnFirstAttempt_ContinuesToSiblingsAndAllowsSameVersionRecoveryForEveryDevice()
    {
        // #755 Kane cycle 3 deterministic coverage — multi-device sync throw.
        //
        // When the sender throws SYNCHRONOUSLY before transport truly starts
        // (i.e., before any awaitable has begun), the failure is a
        // pre-transport per-device event. The dispatcher must:
        //   (a) continue to sibling devices in the same fan-out; and
        //   (b) leave every synchronously-failed device eligible for
        //       exact-version recovery via a subsequent DispatchAsync at
        //       the same version.
        //
        // On the rejected candidate, the outer per-owner device loop bails
        // as soon as SendAndApplyForDeviceAsync returns DispatchStopped —
        // which the IsCurrent check produces after a sync-throw because no
        // snapshot was committed. Sibling B is therefore never attempted.
        // A subsequent same-version dispatch is fenced once any earlier
        // device did commit the snapshot. The invariant proved here is that
        // BOTH devices must be reachable on the fan-out and BOTH must retry
        // when their prior attempt never truly began transport.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        DeviceToken deviceA = MakeToken(userId, "kane-multi-device-a");
        DeviceToken deviceB = MakeToken(userId, "kane-multi-device-b");
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { userId });
        tokens.Setup(r => r.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken> { deviceA, deviceB });
        var successWrites = new ConcurrentBag<Guid>();
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, long, DateTime, CancellationToken>((id, _, _, _) => successWrites.Add(id))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var attemptedDevices = new ConcurrentQueue<Guid>();
        var perDeviceCounts = new ConcurrentDictionary<Guid, int>();
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            Guid tokenId = Guid.Parse(envelope.DeviceTokenId);
            attemptedDevices.Enqueue(tokenId);
            int attempt = perDeviceCounts.AddOrUpdate(tokenId, 1, (_, prev) => prev + 1);
            if (attempt == 1)
            {
                // This typed sender intentionally throws before it signals
                // TryStart, modeling a pre-transport preparation failure.
                throw new InvalidOperationException(
                    $"kane #755 cycle 3: simulated synchronous sender failure before transport (device={tokenId})");
            }

            (await transportStart.TryStartAsync(cancellationToken)).IsPermitted.Should().BeTrue();
            return NativePushDispatchResult.Delivered();
        });

        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        DateTime occurredAt = new(2026, 7, 14, 22, 30, 0, DateTimeKind.Utc);

        // Dispatch #1 at v1 — the sender throws synchronously for the first
        // device it is invoked with. Sibling continuation invariant: the
        // second device must ALSO be attempted in the same fan-out.
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);
        Guid[] firstDispatchAttempts = attemptedDevices.ToArray();
        firstDispatchAttempts.Should().BeEquivalentTo(
            new[] { deviceA.Id, deviceB.Id },
            "the sender's synchronous throw for device A must be isolated at the per-device boundary; device B must still be attempted in the SAME fan-out (sibling continuation)");

        // Dispatch #2 at the SAME v1 — exact-version recovery invariant.
        // Because NEITHER device truly started transport on the first
        // dispatch (their sync throws were rolled back inside TryBeginSend),
        // no per-device lifecycle commit may fence the retry. Every device
        // must retry at the same version and deliver on this attempt.
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);
        Guid[] allAttempts = attemptedDevices.ToArray();
        allAttempts.Should().HaveCount(
            4,
            "the exact-version retry must invoke the sender a second time for BOTH devices — the first attempt's sync throw never truly started transport, so no lifecycle commit may fence the retry for either device");
        perDeviceCounts[deviceA.Id].Should().Be(2, "device A: 1 sync throw + 1 delivered retry");
        perDeviceCounts[deviceB.Id].Should().Be(2, "device B: 1 sync throw + 1 delivered retry");

        // Every retried device must persist its success.
        successWrites.Should().BeEquivalentTo(new[] { deviceA.Id, deviceB.Id });
        tokens.Verify(r => r.RecordFailureAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a synchronous pre-transport throw is never evidence against a token's health");
        tokens.Verify(r => r.InvalidateAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_AsyncFaultedTaskBeforeTransport_AllowsSameVersionRetry()
    {
        // #755 remediation blocker 2: when an async sender's returned Task
        // is ALREADY Faulted at the moment TryBeginSend observes it — i.e.
        // the sender never yielded and never truly reached transport,
        // instead of throwing synchronously it returned a
        // Task.FromException-shaped result — the lifecycle must NOT commit
        // _latestCommitted / _snapshot / dedupe / rate. This is the
        // realistic failure mode for any `async Task<...> SendAsync(...)`
        // sender (both concrete senders in this codebase — Relay and
        // DirectApns — are declared `async`): an exception raised before
        // the method's first await is captured into the returned Task by
        // C#'s async method semantics rather than thrown to this caller, so
        // TryBeginSend's synchronous-throw-only catch never observes it.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        int attemptCount = 0;
        var sender = new DelegateTransportSender(async (_, transportStart, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attemptCount) == 1)
            {
                // A completed fault is not a transport fact. The typed
                // sender has deliberately not signaled the boundary.
                throw new InvalidOperationException("simulated pre-transport async failure");
            }

            (await transportStart.TryStartAsync(cancellationToken)).IsPermitted.Should().BeTrue();
            return NativePushDispatchResult.Delivered();
        });

        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        DateTime occurredAt = new(2026, 7, 14, 20, 45, 0, DateTimeKind.Utc);

        // First dispatch: the sender's Task is already Faulted when
        // TryBeginSend observes it. With the fix, the lifecycle rolls back
        // dedupe + rate reservations and leaves _latestCommitted false.
        // With the bug, TryBeginSend committed as soon as startSend()
        // returned any Task without a synchronous throw, poisoning the
        // exact-version retry below.
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);

        // Second dispatch with the SAME version must proceed and deliver.
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);

        attemptCount.Should().Be(
            2,
            "the exact-version retry must reach the sender again after the first attempt's Task completed unsuccessfully before any transport truly started");
        tokens.Verify(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_MultiDeviceAsyncFaultedTaskForEveryDeviceOnFirstAttempt_ContinuesToSiblingsAndAllowsSameVersionRecoveryForEveryDevice()
    {
        // #755 remediation blocker 2 — multi-device async-faulted-Task
        // coverage, mirroring
        // DispatchAsync_MultiDeviceSyncSenderExceptionForEveryDeviceOnFirstAttempt_ContinuesToSiblingsAndAllowsSameVersionRecoveryForEveryDevice
        // but for the Task.FromException (no synchronous throw) path.
        // Proves both (a) sibling continuation within the SAME fan-out and
        // (b) exact-version recovery for EVERY device, not just device A.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        DeviceToken deviceA = MakeToken(userId, "async-fault-multi-device-a");
        DeviceToken deviceB = MakeToken(userId, "async-fault-multi-device-b");
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { userId });
        tokens.Setup(r => r.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken> { deviceA, deviceB });
        var successWrites = new ConcurrentBag<Guid>();
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, long, DateTime, CancellationToken>((id, _, _, _) => successWrites.Add(id))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var attemptedDevices = new ConcurrentQueue<Guid>();
        var perDeviceCounts = new ConcurrentDictionary<Guid, int>();
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            Guid tokenId = Guid.Parse(envelope.DeviceTokenId);
            attemptedDevices.Enqueue(tokenId);
            int attempt = perDeviceCounts.AddOrUpdate(tokenId, 1, (_, prev) => prev + 1);
            if (attempt == 1)
            {
                throw new InvalidOperationException(
                    $"simulated pre-transport async failure (device={tokenId})");
            }

            (await transportStart.TryStartAsync(cancellationToken)).IsPermitted.Should().BeTrue();
            return NativePushDispatchResult.Delivered();
        });

        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        DateTime occurredAt = new(2026, 7, 14, 23, 15, 0, DateTimeKind.Utc);

        // Dispatch #1 at v1 — both devices' Tasks are already Faulted on
        // their first attempt. Sibling continuation invariant: device B
        // must still be attempted in the SAME fan-out as device A.
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);
        Guid[] firstDispatchAttempts = attemptedDevices.ToArray();
        firstDispatchAttempts.Should().BeEquivalentTo(
            new[] { deviceA.Id, deviceB.Id },
            "device A's already-faulted Task must be isolated at the per-device boundary; device B must still be attempted in the SAME fan-out (sibling continuation)");

        // Dispatch #2 at the SAME v1 — exact-version recovery invariant.
        // Neither device truly started transport on the first dispatch (no
        // lifecycle commit was made because both Tasks completed
        // unsuccessfully before TryBeginSend observed them), so no
        // per-device lifecycle commit may fence the retry. Every device
        // must retry at the same version and deliver on this attempt.
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);
        Guid[] allAttempts = attemptedDevices.ToArray();
        allAttempts.Should().HaveCount(
            4,
            "the exact-version retry must invoke the sender a second time for BOTH devices — neither device's first attempt ever truly started transport, so no lifecycle commit may fence the retry for either device");
        perDeviceCounts[deviceA.Id].Should().Be(2, "device A: 1 async fault + 1 delivered retry");
        perDeviceCounts[deviceB.Id].Should().Be(2, "device B: 1 async fault + 1 delivered retry");

        successWrites.Should().BeEquivalentTo(new[] { deviceA.Id, deviceB.Id });
        tokens.Verify(r => r.RecordFailureAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "an already-faulted pre-transport Task is never evidence against a token's health");
        tokens.Verify(r => r.InvalidateAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_AsyncPreCanceledTaskBeforeTransport_PropagatesButAllowsSameVersionRetry()
    {
        // #755 remediation blocker 2 — Task.FromCanceled / pre-cancelled
        // async-return path, the second completion state (distinct from
        // Faulted) that must roll back a pre-transport reservation. An
        // unrelated/internal cancellation (NOT the caller's own
        // cancellationToken) must still propagate out of DispatchAsync
        // unchanged — see
        // DispatchAsync_SenderThrowsOceWithInternalToken_PropagatesWhenCallerTokenIsNone,
        // which locks in that the dispatcher must not guess that every
        // unrelated OperationCanceledException is a benign timeout, and
        // this test does not alter that contract. What this test proves is
        // the orthogonal half of blocker 2: even though the exception still
        // propagates, TryBeginSend must not have committed the
        // lifecycle/dedupe/rate for that failed attempt, so a subsequent
        // exact-version DispatchAsync can still recover and actually
        // deliver — distinguishing "no transport occurred" from a genuine
        // attempted send.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        using var innerCts = new CancellationTokenSource();
        innerCts.Cancel();

        int attemptCount = 0;
        var sender = new DelegateTransportSender(async (_, transportStart, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attemptCount) == 1)
            {
                // The sender returns a canceled task without signaling the
                // transport boundary; the dispatcher must roll back first,
                // then preserve the established cancellation contract.
                throw new OperationCanceledException(innerCts.Token);
            }

            (await transportStart.TryStartAsync(cancellationToken)).IsPermitted.Should().BeTrue();
            return NativePushDispatchResult.Delivered();
        });

        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        DateTime occurredAt = new(2026, 7, 14, 23, 45, 0, DateTimeKind.Utc);

        // First dispatch: sender returns an already-canceled Task. The
        // dispatcher must not guess this is benign, so it propagates —
        // matching the established, tested contract for unrelated OCEs.
        Func<Task> firstAttempt = () => sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: occurredAt,
            cancellationToken: CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<OperationCanceledException>();

        // Second dispatch, SAME version: must still be able to deliver.
        // With the fix, the first attempt's pre-transport cancellation was
        // rolled back before it propagated, so this is not fenced as stale.
        // With the bug, TryBeginSend had already committed _latestCommitted
        // as soon as startSend() returned any Task, permanently fencing
        // this exact version even though transport never truly started.
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);

        attemptCount.Should().Be(
            2,
            "the exact-version retry must reach the sender again after the first attempt's pre-transport cancellation, even though that cancellation propagated out of the first DispatchAsync call");
        tokens.Verify(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_RateLimitedVersionlessDedupeSurvivesStalledGlobalResolutionAtomicReset_NewerTargetedCreatedMustEmitInterleavingA()
    {
        // #755 Kane cycle 3 deterministic coverage — dedupe-reset race
        // interleaving (a): "Resolution is INVOKED before the newer same-kind
        // reservation, but its atomic reset is asynchronously stalled BEFORE
        // it can fire, so the reset is stale/late by the time the newer
        // reservation attempts its dedupe check."
        //
        // Concretely:
        //   1. The (user, printer, kind) rate bucket is pre-filled by a
        //      prior delivered dispatch of a DIFFERENT attention item (a
        //      "rate filler") that shares the rate key. v1 targeted
        //      Created for the item under test then hits the rate limit
        //      and takes the "rate-limited/no-transport, versionless-
        //      reservation" path: TryBeginSend.shouldEmit commits the
        //      (user, item, Created) dedupe entry via AddOrUpdate,
        //      tryConsumeRate returns false, and TryBeginSend sets
        //      _latestCommitted=true and returns LifecycleSendBlockReason.
        //      RateLimit. No envelope is sent for v1; _snapshot stays
        //      null; the dedupe entry is intentionally retained per the
        //      current rate-limit branch's comment.
        //   2. A global v2 Resolved for the same item is dispatched
        //      (targetUserId=null). It stalls inside GetActiveTokenOwners
        //      Async via an explicit TaskCompletionSource barrier — BEFORE
        //      it can reach any DispatchForOwnerAsync's TryObserveLifecycle,
        //      and therefore BEFORE the onResolvedObserved callback can
        //      fire the atomic dedupe reset for the item's per-user
        //      lifecycle. Global Resolved is the ONLY caller of
        //      GetActiveTokenOwnersAsync in the dispatch pipeline;
        //      targeted dispatches take the explicit-owner path at
        //      DispatchCoreAsync's `owners = new[] { explicitUser }`
        //      branch and never enter that method.
        //   3. While v2 is stalled, a newer targeted v3 Created for user A
        //      on the same item is dispatched. Targeted runs on
        //      lane_(A, item), which is a DIFFERENT AttentionDispatchLane
        //      than v2's lane_(null, item), so v3 is not serialised behind
        //      v2's stalled semaphore. v3's TryObserve accepts (v3 > v1),
        //      bumps _latest to v3 AND resets _latestCommitted=false on
        //      the version bump, but Created does NOT fire the atomic
        //      dedupe reset (only Resolved does). v3 then reaches
        //      TryBeginSend.shouldEmit, which examines the versionless
        //      (user, item, Created) dedupe entry.
        //   4. On the rejected candidate 2c3771cd, v1's still-valid entry
        //      blocks v3: shouldEmit returns false → TryBeginSend sets
        //      _latestCommitted=true and returns LifecycleSendBlockReason.
        //      Dedupe → v3 NOT sent. When v2's gate finally releases, v2
        //      fetches its owner list, unions with lifecycle owners (A is
        //      present via v1's lifecycle), and DispatchForOwnerAsync(A)'s
        //      TryObserveLifecycle observes _latest=v3 > v2 → Stale, so
        //      onResolvedObserved does NOT fire (the reset is now
        //      stale/too late for v3). No envelope is ever emitted for
        //      this item — the rate-filler's envelope for the DIFFERENT
        //      item is not what the invariant protects.
        //   5. On a fixed candidate v3 emits despite v1's older versionless
        //      dedupe entry. Every viable fix — clearing prior-version
        //      dedupe when the lifecycle version bumps, making shouldEmit
        //      version-aware, or rolling back the rate-limit dedupe
        //      reservation — converges on v3 being sent because none of
        //      them let an older-version versionless entry suppress a
        //      strictly-newer legitimate occurrence.
        //
        // This is GENUINELY DISTINCT from interleaving B in call ordering,
        // stall mechanism, and prior-generation state:
        //   - B invokes the newer Created BEFORE Resolution is dispatched,
        //     so v3 races ahead of a not-yet-invoked reset in a purely
        //     sequential-await ordering. A invokes Resolution FIRST and
        //     relies on Resolution being asynchronously stalled BEFORE its
        //     already-invoked reset can fire — a global-vs-targeted lane
        //     split plus an explicit async gate, not a serial call swap.
        //   - B's v1 is DELIVERED (has both dedupe entry AND committed
        //     snapshot). A's v1 is RATE-LIMITED (has dedupe entry, NO
        //     snapshot). Any fix must handle both prior states.
        // Both cover the same underlying versionless-dedupe suppression
        // invariant from complementary failure modes.
        //
        // Determinism: ordering is enforced by TaskCompletionSource
        // barriers — no timing sleeps. The rate bucket is pre-primed to
        // force v1's rate-limit branch deterministically. The lane
        // separation (targeted vs global) is what lets v3 execute while
        // v2 is stalled without contending on a shared semaphore.
        Guid userId = Guid.NewGuid();
        Guid sharedPrinterId = Guid.NewGuid();
        AttentionItemDto rateFiller = BuildAttentionItem(AttentionKind.Offline) with
        {
            PrinterId = sharedPrinterId,
            PrinterName = "Kane A rate-filler printer",
        };
        AttentionItemDto initial = BuildAttentionItem(AttentionKind.Offline) with
        {
            PrinterId = sharedPrinterId,
            PrinterName = "Kane A initial printer",
        };
        AttentionItemDto recurrence = initial with
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "Kane A recurrence printer",
        };

        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);

        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(r => r.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    InstallationId = "test-install",
                    Token = "AA".PadRight(64, 'A'),
                    Platform = "ios",
                    Environment = "development",
                    IsActive = true,
                },
            });
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // GetActiveTokenOwnersAsync is called ONLY by the global Resolved
        // dispatch inside DispatchCoreAsync (targeted dispatches skip it
        // via the explicit-owner branch). We gate v2's single call to this
        // method via a TaskCompletionSource barrier so v2 stalls BEFORE
        // any DispatchForOwnerAsync (and thus BEFORE any onResolvedObserved
        // reset for A) can fire.
        var globalOwnersGateEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseGlobalOwnersGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        tokens.Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async ct =>
            {
                globalOwnersGateEntered.TrySetResult();
                await releaseGlobalOwnersGate.Task.WaitAsync(ct);
                return (IReadOnlyList<Guid>)new List<Guid> { userId };
            });

        int initialLookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(s => s.FindItemAsync(userId, rateFiller.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rateFiller);
        attention.Setup(s => s.FindItemAsync(userId, initial.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int call = Interlocked.Increment(ref initialLookupCount);
                return call switch
                {
                    1 => initial,       // v1 targeted Created (about to be rate-limited)
                    2 => recurrence,    // v3 targeted newer Created (must emit)
                    _ => null,          // v2 never reaches FindItemAsync — TryObserveLifecycle returns Stale first
                };
            });

        var sent = new ConcurrentQueue<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender.Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) => sent.Enqueue(envelope))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        // RateLimitPerUser=1 lets a single prior same-(user, printer, kind)
        // dispatch pre-fill the bucket so v1 targeted Created is
        // deterministically rate-limited. DedupeWindow is generous
        // relative to the inter-dispatch spacing so v1's versionless entry
        // cannot wall-clock expire before v3's TryBeginSend examines it —
        // shouldEmit uses TimeProvider.UtcNow (real time in this test) and
        // the whole scenario executes in well under a second.
        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings
            {
                Mode = NativePushMode.Relay,
                DedupeWindow = TimeSpan.FromMinutes(10),
                RateLimitPerUser = 1,
                RateLimitWindow = TimeSpan.FromMinutes(10),
            });
        DateTime t0 = new(2026, 7, 14, 22, 45, 0, DateTimeKind.Utc);

        // Step 0: pre-fill the (userId, sharedPrinterId, Offline) rate
        // bucket by delivering a targeted Created for a DIFFERENT attention
        // item that shares the rate key. Its dedupe key is per-item, so it
        // does not interfere with v1's dedupe reservation on `initial.Id`.
        await sut.DispatchAsync(rateFiller.Id, AttentionChangeKind.Created, userId, occurredAtUtc: t0);

        // Step 1: v1 targeted Created for `initial` at t1 > t0. The rate
        // bucket now holds one timestamp (from Step 0), so TryBeginSend
        // commits the versionless (userId, initial.Id, Created) dedupe
        // entry via shouldEmit, then tryConsumeRate returns false and
        // TryBeginSend returns LifecycleSendBlockReason.RateLimit. No
        // envelope is sent for v1; the lifecycle records _latest=v1,
        // _latestCommitted=true, _snapshot stays null, and the dedupe
        // entry is intentionally retained per the rate-limit branch's
        // stated invariant.
        await sut.DispatchAsync(initial.Id, AttentionChangeKind.Created, userId, occurredAtUtc: t0.AddSeconds(1));

        // Step 2: v2 global Resolved for `initial` at t2 > t1. Do NOT
        // await — it will stall inside the mocked GetActiveTokenOwnersAsync
        // BEFORE it can reach DispatchForOwnerAsync's TryObserveLifecycle,
        // so onResolvedObserved has NOT fired yet when the barrier
        // completes. Explicit TCS awaits sequence the ordering without
        // any timing sleep.
        Task resolvedTask = sut.DispatchAsync(
            initial.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: t0.AddSeconds(2));
        await globalOwnersGateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Step 3: v3 newer targeted Created for user A on `initial` at
        // t3 > t2 > t1. Targeted takes the explicit-owner path (never
        // enters GetActiveTokenOwnersAsync) and runs on lane_(A, initial),
        // which is different from v2's lane_(null, initial). v3's
        // TryObserve accepts and bumps _latest to v3 while resetting
        // _latestCommitted to false, but Created does NOT fire the atomic
        // dedupe reset (only Resolved does). v3 then reaches
        // TryBeginSend.shouldEmit and examines the versionless
        // (userId, initial.Id, Created) dedupe entry.
        //
        // On the rejected candidate 2c3771cd, v1's still-valid entry
        // causes shouldEmit to return false → LifecycleSendBlockReason.
        // Dedupe → v3 is suppressed and NO envelope is sent for v3.
        await sut.DispatchAsync(initial.Id, AttentionChangeKind.Created, userId, occurredAtUtc: t0.AddSeconds(3));

        // Step 4: capture the state BEFORE v2's gate is released. This
        // is the crucial observation window: on the rejected candidate,
        // v3 has already been dropped because v2's atomic reset has not
        // yet fired. The rate-filler envelope is filtered out; only
        // envelopes for the item under test matter for the invariant.
        NativePushEnvelope[] initialEnvelopesBeforeRelease = sent
            .Where(e => e.AttentionItemId == initial.Id)
            .ToArray();

        // Step 5: release v2 and let it complete. On BOTH rejected and
        // fixed candidates, v2's TryObserveLifecycle observes _latest=v3
        // > v2 and returns Stale (v2's timestamp precedes v3's), so
        // onResolvedObserved does NOT fire and no silent dismissal is
        // emitted. This step drains the outstanding task cleanly; it is
        // not what the invariant asserts.
        releaseGlobalOwnersGate.TrySetResult();
        await resolvedTask.WaitAsync(TimeSpan.FromSeconds(10));

        NativePushEnvelope[] initialEnvelopesFinal = sent
            .Where(e => e.AttentionItemId == initial.Id)
            .ToArray();

        // Invariant: v3's newer Created MUST have emitted during Step 3,
        // despite v1's rate-limited versionless dedupe reservation and
        // v2's asynchronously-stalled atomic reset. On the rejected
        // candidate this assertion fails because v3 was suppressed
        // before v2's reset could catch up.
        initialEnvelopesBeforeRelease.Should().ContainSingle(
            "v3's newer Created must not be suppressed by v1's stale versionless dedupe entry when v2's atomic reset is asynchronously stalled — the reset arrived too late to unblock v3")
            .Which.PrinterId.Should().Be(recurrence.PrinterId,
                "the recurrence printer id proves v3 emitted (not a duplicate replay of v1's initial-printer envelope)");
        initialEnvelopesBeforeRelease[0].ChangeKind.Should().Be(AttentionChangeKind.Created);
        initialEnvelopesBeforeRelease[0].Priority.Should().Be(NativePushPriority.Alert);

        // v2 becomes Stale after release (v2 < v3 by lifecycle version),
        // so no additional envelope is added for `initial`. The final
        // state matches the pre-release state on both rejected and fixed
        // candidates; the rate-filler envelope for the DIFFERENT attention
        // item is unrelated to this invariant.
        initialEnvelopesFinal.Should().Equal(
            initialEnvelopesBeforeRelease,
            "v2 was stalled long enough to observe a superseded lifecycle version and become Stale; no silent dismissal must be emitted for `initial` after release");
    }

    [Fact]
    public async Task DispatchAsync_NewerCreatedReservationAttemptedBeforeResolutionRacesIn_NewerMustEmitDespitePriorGenerationVersionlessDedupeInterleavingB()
    {
        // #755 Kane cycle 3 deterministic coverage — dedupe-reset race
        // interleaving (b): "Resolution races AFTER the newer same-kind
        // reservation is established."
        //
        // Sequential awaits establish observable ordering without timing
        // sleeps: v1 Created is fully dispatched, THEN v2 newer Created is
        // dispatched (its reservation attempt is "established" — TryObserve
        // and TryBeginSend both run), THEN v3 Resolved arrives.
        //
        // Invariant under test: a rate-limited/no-transport OR delivered
        // previous same-kind generation's versionless (user, item, Created)
        // dedupe entry MUST NOT suppress a legitimate newer occurrence
        // whose lifecycle version is strictly greater. Resolution has NOT
        // yet fired when v2 attempts to reserve, so the reset cannot help
        // — the invariant must hold on its own.
        //
        // On the rejected candidate:
        //   - v1 delivers → dedupe entry v1_created retained.
        //   - v2 TryObserveLifecycle accepts (v2 > v1), then TryBeginSend's
        //     shouldEmit finds v1's versionless dedupe entry and returns
        //     false → LifecycleSendBlockReason.Dedupe → v2 NOT sent.
        //   - v3 Resolved races in after v2's suppressed reservation and
        //     fires the reset, but it is too late for v2.
        // On a fixed candidate: v2 emits (invariant preserved).
        Guid userId = Guid.NewGuid();
        AttentionItemDto initial = BuildAttentionItem(AttentionKind.Offline);
        AttentionItemDto recurrence = initial with
        {
            PrinterId = Guid.NewGuid(),
            PrinterName = "Kane newer-Created printer",
        };
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        int lookupCount = 0;
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                initial.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int call = Interlocked.Increment(ref lookupCount);
                return call switch
                {
                    1 => initial,     // v1 Created
                    2 => recurrence,  // v2 newer Created (must emit)
                    _ => null,        // v3 Resolved: source dropped item
                };
            });

        var sent = new ConcurrentQueue<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender.Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) => sent.Enqueue(envelope))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        // DedupeWindow is much larger than the inter-dispatch spacing so
        // any lingering versionless dedupe entry from v1 is guaranteed to
        // still be within-window when v2's TryBeginSend checks it. If the
        // rejected candidate suppressed v2 by wall-clock expiry rather
        // than by version-aware logic, this window forces the failure to
        // reproduce deterministically.
        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings
            {
                Mode = NativePushMode.Relay,
                DedupeWindow = TimeSpan.FromMinutes(10),
                RateLimitPerUser = 50,
                RateLimitWindow = TimeSpan.FromMinutes(10),
            });
        DateTime t1 = new(2026, 7, 14, 22, 40, 0, DateTimeKind.Utc);

        // Step 1: v1 Created at t1 fully delivers. Sequential await
        // guarantees v1's snapshot and dedupe entry are committed before
        // step 2. The (user, item, Created) key is now in _dedupe with
        // expiry t1 + DedupeWindow — a versionless reservation.
        await sut.DispatchAsync(initial.Id, AttentionChangeKind.Created, userId, occurredAtUtc: t1);

        // Step 2: v2 newer Created at t2 > t1. Sequential await ensures
        // v2's "reservation is established" (TryObserveLifecycle accepts
        // v2 > v1; TryBeginSend is entered) BEFORE Resolution arrives in
        // step 3. This is the "newer same-kind reservation established"
        // clause of interleaving (b).
        //
        // The rejected candidate suppresses this dispatch at TryBeginSend's
        // shouldEmit because v1's versionless dedupe entry is still within
        // window. That is the concrete bug this assertion pins.
        await sut.DispatchAsync(initial.Id, AttentionChangeKind.Created, userId, occurredAtUtc: t1.AddSeconds(1));

        // Step 3: v3 Resolved at t3 > t2 races in AFTER v2's reservation
        // is established. Its onResolvedObserved would clear the Created
        // dedupe entry, but on the rejected candidate v2 has already been
        // suppressed by v1's entry. A silent dismissal is still expected
        // if a snapshot exists (v1's snapshot, still present on rejected
        // because v2 never committed a new one; v2's snapshot on a fixed
        // candidate).
        await sut.DispatchAsync(initial.Id, AttentionChangeKind.Resolved, userId, occurredAtUtc: t1.AddSeconds(2));

        NativePushEnvelope[] captured = sent.ToArray();
        captured.Select(e => e.ChangeKind).Should().Equal(
            new[]
            {
                AttentionChangeKind.Created,
                AttentionChangeKind.Created,
                AttentionChangeKind.Resolved,
            },
            "v2's newer Created reservation must not be suppressed by v1's versionless dedupe entry; the invariant fails if v2 is silently dropped");
        captured[0].PrinterId.Should().Be(initial.PrinterId);
        captured[1].PrinterId.Should().Be(recurrence.PrinterId,
            "the second Created envelope must carry the recurrence printer id, proving v2 emitted (not a duplicate replay of v1)");
        captured[1].ChangeKind.Should().Be(AttentionChangeKind.Created);
        captured[1].Priority.Should().Be(NativePushPriority.Alert);
        captured[2].AttentionItemId.Should().Be(initial.Id);
        captured[2].Priority.Should().Be(NativePushPriority.Background);
    }

    [Fact]
    public async Task DispatchAsync_GlobalResolvedWithoutPriorLifecycleForTokenlessRecipient_ReRegistrationCannotResurrectStaleTargetedAlert()
    {
        // #755 Kane cycle 3 deterministic coverage — tokenless recipient
        // whose targeted dispatch has NOT yet installed its lifecycle.
        //
        // Sequence (deterministic via sequential awaits and a
        // TaskCompletionSource re-registration barrier):
        //   1. User A has no active device tokens AND no prior lifecycle
        //      entry for the attention item (targeted dispatch has never
        //      run for this (user, item) pair).
        //   2. A global Resolved arrives. Because both
        //      GetActiveTokenOwnersAsync and GetOwnersWithLifecycleFor are
        //      empty for user A, the resolution installs no lifecycle
        //      tombstone for user A on the rejected candidate.
        //   3. User A re-registers, receiving a fresh device token.
        //   4. A stale targeted Created arrives for user A with an
        //      OccurredAt strictly earlier than the resolution. This
        //      simulates a delayed queue drain, a retried source event,
        //      or a batched targeted dispatch that landed after global
        //      resolution.
        //
        // Invariant: stale targeted delivery to a re-registered tokenless
        // recipient MUST NOT occur after a global resolution.
        //
        // On the rejected candidate: user A's fresh lifecycle at step 4 is
        // brand new (_hasVersion=false), so TryObserveLifecycle accepts the
        // stale targeted Created's version unconditionally and the alert
        // is delivered — a stale-send bug.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);

        // Explicit re-registration gate: tokens are absent until we release
        // this TaskCompletionSource, then present with a fresh registration
        // for the stale targeted retry. No timing sleep — the flip is
        // triggered by the test setter between dispatches.
        var reRegistered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DeviceToken? reRegisteredToken = null;
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (reRegistered.Task.IsCompletedSuccessfully)
                {
                    return Task.FromResult<IReadOnlyList<Guid>>(new List<Guid> { userId });
                }

                return Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
            });
        tokens.Setup(r => r.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (reRegisteredToken is DeviceToken current)
                {
                    return Task.FromResult<IReadOnlyList<DeviceToken>>(new List<DeviceToken> { current });
                }

                return Task.FromResult<IReadOnlyList<DeviceToken>>(Array.Empty<DeviceToken>());
            });
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Source-of-truth attention lookup: on the stale targeted retry the
        // source has not yet propagated the resolution, so FindItemAsync
        // still returns the live item. If the dispatcher had no
        // tombstone/version fence, this is exactly the state that lets a
        // stale alert land on the re-registered device.
        var attention = new Mock<IAttentionService>();
        attention.Setup(s => s.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        var sent = new ConcurrentQueue<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender.Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) => sent.Enqueue(envelope))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        NativePushDispatcher sut = BuildWithScope(
            sender, gate.Object, tokens.Object, attention.Object, db,
            new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                MaxAttempts = 1,
            });
        DateTime resolvedAt = new(2026, 7, 14, 22, 45, 0, DateTimeKind.Utc);
        DateTime staleTargetedAt = resolvedAt.AddSeconds(-30);

        // Step 1 & 2: global Resolved arrives while user A is tokenless
        // AND has no prior lifecycle for this item. The rejected candidate
        // installs no lifecycle tombstone for user A.
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt);
        sent.Should().BeEmpty(
            "the global resolution has no owners to dispatch to — a tokenless recipient with no prior lifecycle cannot receive a silent dismissal");

        // Step 3: user A re-registers via the explicit barrier. The
        // dispatcher will now see user A as an active token owner for any
        // subsequent lookup — no timing sleep is used.
        reRegisteredToken = MakeToken(userId, "kane-tokenless-reregister");
        reRegistered.TrySetResult();

        // Step 4: a stale targeted Created arrives for user A at
        // staleTargetedAt < resolvedAt. The invariant fails on the
        // rejected candidate: the dispatcher installs a fresh lifecycle
        // for (user, item) and delivers the stale alert to the newly
        // re-registered device.
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: staleTargetedAt);

        sent.Should().BeEmpty(
            "a stale targeted alert arriving after a global resolution — even when the recipient's lifecycle was not installed at resolution time — must not deliver after re-registration; the version fence must survive tokenless resolution");
        sender.Verify(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no envelope may reach the sender for the stale targeted retry");
    }

    [Fact]
    public async Task DispatchAsync_ConcurrentGlobalResolvedDuringTargetedLifecycleInstall_FencesRegardlessOfInterleaving()
    {
        // #755 remediation blocker 1 (P-A-D-R-S): deterministic regression
        // for the item-tombstone TOCTOU that the rejected candidate left
        // open. Unlike
        // DispatchAsync_GlobalResolvedWithoutPriorLifecycleForTokenlessRecipient_ReRegistrationCannotResurrectStaleTargetedAlert
        // (fully sequential: the global Resolved completes entirely BEFORE
        // the stale targeted Created is even dispatched) this test
        // orchestrates genuine, deterministic concurrency:
        //
        // P. A stale targeted Created (v1) for a tokenless, never-before-
        //    dispatched recipient starts. It is deterministically paused —
        //    via a gated IServiceScopeFactory — after DispatchAsync's own
        //    (at-that-moment-correct) tombstone read, but strictly BEFORE
        //    it reaches TryObserveLifecycle, the point that installs its
        //    per-owner lifecycle. (TryObserveLifecycle is the very first
        //    statement once a targeted dispatch reaches its owner; the only
        //    genuine synchronisation point before it is scope creation.)
        // A. While P is paused, a concurrent global Resolved (v2 > v1) runs
        //    to completion and publishes the item-wide tombstone.
        // D. That SAME Resolved dispatch enumerates lifecycle owners while
        //    the recipient is tokenless AND before P has installed any
        //    lifecycle — it finds no owner for this recipient and
        //    completes without sending anything.
        // R. The recipient re-registers a device token.
        // S. P is released and resumes: on the rejected design (a one-time
        //    tombstone check at DispatchAsync's entry, already read as
        //    absent before A published) it installs a fresh lifecycle
        //    unaware of the resolution, fetches the now-present token, and
        //    sends the stale alert AFTER the resolution. With the fix,
        //    AttentionItemFence's shared lock means P's resumed
        //    TryObserveLifecycle call re-checks the SAME, now-published
        //    tombstone atomically with its own lifecycle install and is
        //    rejected before any transport.
        //
        // Mutation proof (see validation notes): reverting
        // AttentionItemFence.TryAdmitTargeted to unconditionally admit (or
        // reverting DispatchAsync/PublishResolvedTombstoneAndEnumerateOwners
        // to the old one-time, non-atomic _resolvedItemVersions check) makes
        // this test fail — P installs a lifecycle and sends unconditionally
        // once released.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        db.NotificationPreferences.Add(new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        });
        await db.SaveChangesAsync();

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);

        bool reRegistered = false;
        DeviceToken? reRegisteredToken = null;
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<IReadOnlyList<Guid>>(
                reRegistered ? new List<Guid> { userId } : Array.Empty<Guid>()));
        tokens.Setup(r => r.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<IReadOnlyList<DeviceToken>>(
                reRegisteredToken is DeviceToken current
                    ? new List<DeviceToken> { current }
                    : Array.Empty<DeviceToken>()));
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var attention = new Mock<IAttentionService>();
        attention.Setup(s => s.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        var sent = new ConcurrentQueue<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender.Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) => sent.Enqueue(envelope))
            .ReturnsAsync(NativePushDispatchResult.Delivered());

        var services = new ServiceCollection();
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton(attention.Object);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory realFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var targetedEnteredScopeCreation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTargetedScopeCreation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedFactory = new FirstCallGatedServiceScopeFactory(
            realFactory,
            targetedEnteredScopeCreation,
            releaseTargetedScopeCreation);

        IOptionsMonitor<NativePushSettings> monitor = new StaticOptionsMonitor(
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        var sut = new NativePushDispatcher(
            gatedFactory,
            AsTransportAwareForTests(sender.Object),
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);

        DateTime resolvedAt = new(2026, 7, 14, 22, 0, 0, DateTimeKind.Utc);
        DateTime staleTargetedAt = resolvedAt.AddSeconds(-30);

        // P: kick off the stale targeted Created on a background thread —
        // its DispatchCoreAsync call is the FIRST call to CreateScope, so
        // the gate blocks it deterministically right there, strictly before
        // TryObserveLifecycle for userId has ever run.
        Task targetedTask = Task.Run(() => sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: staleTargetedAt));

        await targetedEnteredScopeCreation.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A + D: the global Resolved runs to full completion while P is
        // genuinely paused. The recipient is tokenless and has no lifecycle
        // yet (P hasn't reached TryObserveLifecycle), so this finds no
        // owner and completes silently.
        await sut.DispatchAsync(
                item.Id,
                AttentionChangeKind.Resolved,
                targetUserId: null,
                occurredAtUtc: resolvedAt)
            .WaitAsync(TimeSpan.FromSeconds(10));

        sent.Should().BeEmpty(
            "the global resolution has no owners yet — the tokenless recipient has no installed lifecycle while the targeted dispatch is paused");

        // R: recipient re-registers a device token.
        reRegisteredToken = MakeToken(userId, "pdrs-tokenless-reregister");
        reRegistered = true;

        // S: release P. On the rejected design it now installs a lifecycle
        // unaware of the resolution and sends. With the fix, the shared
        // AttentionItemFence rejects it before any lifecycle install/send.
        releaseTargetedScopeCreation.TrySetResult();
        await targetedTask.WaitAsync(TimeSpan.FromSeconds(10));

        sent.Should().BeEmpty(
            "a stale targeted Created racing a concurrent global Resolved must never install a lifecycle and transport after the resolution, regardless of which side reaches its own critical section first");
        sender.Verify(
            s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "no envelope may reach the sender once the resolution has published its tombstone, even if it published it while the targeted dispatch was already mid-flight");
    }

    [Fact]
    public async Task DispatchAsync_GlobalResolvedWaitsForInFlightSuccessfulTransportAndDismissesExactlyOnce()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                item.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        var firstTransportStarted = new TaskCompletionSource<NativePushEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTransport = new TaskCompletionSource<NativePushDispatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        int sendCount = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            sent.Enqueue(envelope);
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                firstTransportStarted.TrySetResult(envelope);
                return await releaseFirstTransport.Task.WaitAsync(cancellationToken);
            }

            return NativePushDispatchResult.Delivered();
        });
        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        var settlementWaitStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sut.OnResolutionSettlementWaitStartedForTests =
            () => settlementWaitStarted.TrySetResult();
        DateTime createdAt = new(2026, 7, 15, 1, 0, 0, DateTimeKind.Utc);

        Task created = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await firstTransportStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task resolved = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: createdAt.AddSeconds(1));

        try
        {
            await settlementWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            resolved.IsCompleted.Should().BeFalse(
                "global resolution must await the captured in-flight alert transport");
            sent.Select(envelope => envelope.ChangeKind).Should().Equal(
                AttentionChangeKind.Created);

            releaseFirstTransport.TrySetResult(NativePushDispatchResult.Delivered());
            await Task.WhenAll(created, resolved).WaitAsync(TimeSpan.FromSeconds(10));

            sent.Select(envelope => envelope.ChangeKind).Should().Equal(
                AttentionChangeKind.Created,
                AttentionChangeKind.Resolved);
            sent.Count(envelope => envelope.ChangeKind == AttentionChangeKind.Resolved)
                .Should().Be(1);
        }
        finally
        {
            releaseFirstTransport.TrySetResult(NativePushDispatchResult.Transient("test-cleanup"));
            await Task.WhenAll(created, resolved).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DispatchAsync_GlobalResolvedWaitsForInFlightFailedTransportAndSkipsDismissal()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                item.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        var firstTransportStarted = new TaskCompletionSource<NativePushEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTransport = new TaskCompletionSource<NativePushDispatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        int sendCount = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            sent.Enqueue(envelope);
            if (Interlocked.Increment(ref sendCount) == 1)
            {
                firstTransportStarted.TrySetResult(envelope);
                return await releaseFirstTransport.Task.WaitAsync(cancellationToken);
            }

            return NativePushDispatchResult.Delivered();
        });
        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        var settlementWaitStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sut.OnResolutionSettlementWaitStartedForTests =
            () => settlementWaitStarted.TrySetResult();
        DateTime createdAt = new(2026, 7, 15, 1, 5, 0, DateTimeKind.Utc);

        Task created = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await firstTransportStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task resolved = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: createdAt.AddSeconds(1));

        try
        {
            await settlementWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            resolved.IsCompleted.Should().BeFalse(
                "resolution must wait for the captured failed transport before deciding no dismissal is needed");
            sent.Select(envelope => envelope.ChangeKind).Should().Equal(
                AttentionChangeKind.Created);

            releaseFirstTransport.TrySetResult(NativePushDispatchResult.Transient("provider_unavailable"));
            await Task.WhenAll(created, resolved).WaitAsync(TimeSpan.FromSeconds(10));

            sent.Select(envelope => envelope.ChangeKind).Should().Equal(
                AttentionChangeKind.Created);
        }
        finally
        {
            releaseFirstTransport.TrySetResult(NativePushDispatchResult.Transient("test-cleanup"));
            await Task.WhenAll(created, resolved).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DispatchAsync_ResolvedWaitsAcrossSupersededGeneration_LateSuccessFromOlderPendingGenerationStillDismissesExactlyOnce()
    {
        // Hicks blocker 1: S1 (targeted Created) starts a real transport and
        // pauses — still pending. A newer global Updated (S2) then supersedes
        // S1's snapshot on the SAME AttentionLifecycle and its OWN transport
        // fails fast, completing entirely before the global Resolved is even
        // dispatched. On the rejected design, Resolved's ResolutionCapture
        // would only ever see S2 (the snapshot active when Resolved was
        // observed) — which has nothing pending and never delivered — so it
        // would skip the dismissal immediately without waiting at all. With
        // the shared-lineage fix, S1's still-pending attempt remains
        // reachable (TryStartTransport reuses the SAME AttentionDeliveryLineage
        // across the S1->S2 snapshot replacement instead of copying a
        // point-in-time bool), so Resolved must still block until S1
        // settles. S1 then succeeds late and the dismissal must still fire —
        // exactly once.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var firstTransportStarted = new TaskCompletionSource<NativePushEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTransport = new TaskCompletionSource<NativePushDispatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        int sendCount = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            int callIndex = Interlocked.Increment(ref sendCount);
            sent.Enqueue(envelope);
            if (callIndex == 1)
            {
                // S1: the stale Created generation. Pauses here — still
                // "pending" from the lineage's perspective — until released
                // further below, well after S2 has already superseded and
                // failed.
                firstTransportStarted.TrySetResult(envelope);
                return await releaseFirstTransport.Task.WaitAsync(cancellationToken);
            }

            if (callIndex == 2)
            {
                // S2: the Updated generation that supersedes S1's snapshot.
                // Fails fast and completes entirely before Resolved is even
                // dispatched.
                return NativePushDispatchResult.Transient("provider_unavailable");
            }

            // The Resolved dismissal itself.
            return NativePushDispatchResult.Delivered();
        });
        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        var settlementWaitStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sut.OnResolutionSettlementWaitStartedForTests =
            () => settlementWaitStarted.TrySetResult();

        DateTime createdAt = new(2026, 7, 15, 3, 0, 0, DateTimeKind.Utc);
        DateTime updatedAt = createdAt.AddSeconds(1);
        DateTime resolvedAt = updatedAt.AddSeconds(1);

        // S1: targeted Created (v1) — starts real transport and pauses.
        Task created = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await firstTransportStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // S2: GLOBAL Updated (v2) — a different dispatch lane than S1's
        // targeted lane, so it can run concurrently while S1 is still
        // paused. Supersedes S1's snapshot on the shared AttentionLifecycle;
        // its own transport fails and the whole dispatch completes here,
        // strictly before Resolved is dispatched.
        await sut.DispatchAsync(
                item.Id,
                AttentionChangeKind.Updated,
                targetUserId: null,
                occurredAtUtc: updatedAt)
            .WaitAsync(TimeSpan.FromSeconds(10));

        sent.Select(envelope => envelope.ChangeKind).Should().Equal(
            AttentionChangeKind.Created,
            AttentionChangeKind.Updated);

        // Resolved (v3, global): must still WAIT — not for S2 (already
        // finished, nothing left pending) but for S1's still-in-flight
        // attempt, inherited through the shared lineage across the S1->S2
        // snapshot replacement.
        Task resolved = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt);

        try
        {
            await settlementWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            resolved.IsCompleted.Should().BeFalse(
                "the resolution must still be waiting on S1's still-pending transport even though S2 — the snapshot active when Resolved was observed — already failed with nothing left pending");

            // S1 succeeds late — after being superseded by S2 and after S2
            // itself already failed and completed.
            releaseFirstTransport.TrySetResult(NativePushDispatchResult.Delivered());
            await Task.WhenAll(created, resolved).WaitAsync(TimeSpan.FromSeconds(10));

            sent.Select(envelope => envelope.ChangeKind).Should().Equal(
                AttentionChangeKind.Created,
                AttentionChangeKind.Updated,
                AttentionChangeKind.Resolved);
            sent.Count(envelope => envelope.ChangeKind == AttentionChangeKind.Resolved)
                .Should().Be(1,
                    "S1's late success must still be attributed to the occurrence and produce exactly one dismissal");
        }
        finally
        {
            releaseFirstTransport.TrySetResult(NativePushDispatchResult.Transient("test-cleanup"));
            await Task.WhenAll(created, resolved).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DispatchAsync_ResolvedWaitsAcrossSupersededGeneration_AllGenerationsFailingSkipsDismissal()
    {
        // Complement of the success case above: every generation across the
        // whole lineage (S1 AND S2) fails, so the resolution must still wait
        // for S1 (proving it is tracked) but must skip the dismissal as a
        // benign no-op once S1's late failure settles.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var firstTransportStarted = new TaskCompletionSource<NativePushEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTransport = new TaskCompletionSource<NativePushDispatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        int sendCount = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            int callIndex = Interlocked.Increment(ref sendCount);
            sent.Enqueue(envelope);
            if (callIndex == 1)
            {
                firstTransportStarted.TrySetResult(envelope);
                return await releaseFirstTransport.Task.WaitAsync(cancellationToken);
            }

            return NativePushDispatchResult.Transient("provider_unavailable");
        });
        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        var settlementWaitStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sut.OnResolutionSettlementWaitStartedForTests =
            () => settlementWaitStarted.TrySetResult();

        DateTime createdAt = new(2026, 7, 15, 3, 10, 0, DateTimeKind.Utc);
        DateTime updatedAt = createdAt.AddSeconds(1);
        DateTime resolvedAt = updatedAt.AddSeconds(1);

        Task created = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await firstTransportStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await sut.DispatchAsync(
                item.Id,
                AttentionChangeKind.Updated,
                targetUserId: null,
                occurredAtUtc: updatedAt)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Task resolved = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt);

        await settlementWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        resolved.IsCompleted.Should().BeFalse(
            "the resolution must still wait on S1 even though it will ultimately fail too");

        releaseFirstTransport.TrySetResult(NativePushDispatchResult.Transient("provider_unavailable"));
        await Task.WhenAll(created, resolved).WaitAsync(TimeSpan.FromSeconds(10));

        sent.Count(envelope => envelope.ChangeKind == AttentionChangeKind.Resolved)
            .Should().Be(0,
                "no device across the whole lineage ever delivered, so the dismissal must be a benign no-op");
    }

    [Fact]
    public async Task DispatchAsync_MultipleSimultaneouslyPendingGenerations_OldestLateSuccessStillDismissesExactlyOnce()
    {
        // Extends the two-generation race to prove the shared lineage's
        // pending set correctly tracks MORE THAN ONE concurrently in-flight
        // generation at once (S1 and S2 are BOTH paused/pending at the same
        // time here, on the two independent dispatch lanes available for a
        // single recipient — targeted and global) rather than only ever
        // holding a single attempt. S2 settles (fails) first, freeing its
        // lane; only then is Resolved dispatched, and it must still wait on
        // the oldest surviving pending generation, S1.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var firstStarted = new TaskCompletionSource<NativePushEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<NativePushDispatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource<NativePushEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource<NativePushDispatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        int sendCount = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            int callIndex = Interlocked.Increment(ref sendCount);
            sent.Enqueue(envelope);
            if (callIndex == 1)
            {
                firstStarted.TrySetResult(envelope);
                return await releaseFirst.Task.WaitAsync(cancellationToken);
            }

            if (callIndex == 2)
            {
                secondStarted.TrySetResult(envelope);
                return await releaseSecond.Task.WaitAsync(cancellationToken);
            }

            return NativePushDispatchResult.Delivered();
        });
        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        var settlementWaitStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sut.OnResolutionSettlementWaitStartedForTests =
            () => settlementWaitStarted.TrySetResult();

        DateTime createdAt = new(2026, 7, 15, 3, 20, 0, DateTimeKind.Utc);
        DateTime updatedAt = createdAt.AddSeconds(1);
        DateTime resolvedAt = updatedAt.AddSeconds(1);

        // S1: targeted Created (v1) — pauses.
        Task created = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // S2: global Updated (v2) — a different lane, so it can start and
        // ALSO pause while S1 is still pending. Both S1 and S2 are now
        // simultaneously tracked as pending on the SAME shared lineage.
        Task updated = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Updated,
            targetUserId: null,
            occurredAtUtc: updatedAt);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        firstStarted.Task.IsCompleted.Should().BeTrue();
        secondStarted.Task.IsCompleted.Should().BeTrue();
        created.IsCompleted.Should().BeFalse("S1 is still paused, mid-transport");

        // Settle S2 (fails) and let its dispatch fully finish, freeing the
        // global lane. Only S1 remains pending afterward.
        releaseSecond.TrySetResult(NativePushDispatchResult.Transient("provider_unavailable"));
        await updated.WaitAsync(TimeSpan.FromSeconds(10));

        // Resolved (v3, global): the lane is free now that S2 finished.
        Task resolved = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt);

        try
        {
            await settlementWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            resolved.IsCompleted.Should().BeFalse(
                "S1 was pending simultaneously with S2 and remains pending after S2 settles — the resolution must still wait for it");

            releaseFirst.TrySetResult(NativePushDispatchResult.Delivered());
            await Task.WhenAll(created, resolved).WaitAsync(TimeSpan.FromSeconds(10));

            sent.Select(envelope => envelope.ChangeKind).Should().Equal(
                AttentionChangeKind.Created,
                AttentionChangeKind.Updated,
                AttentionChangeKind.Resolved);
            sent.Count(envelope => envelope.ChangeKind == AttentionChangeKind.Resolved)
                .Should().Be(1);
        }
        finally
        {
            releaseFirst.TrySetResult(NativePushDispatchResult.Transient("test-cleanup"));
            await Task.WhenAll(created, resolved).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DispatchAsync_ResolutionRetryAfterCancelDuringSettlementWait_AdmitsAndDismissesOnLateS1Success()
    {
        // #755 Hicks (lifecycle-admission variant): a version-blind
        // AttentionLifecycle participant count rejects an exact-latest
        // resolution retry while an older, still-pending generation keeps
        // the total > 0. Deterministic interleaving:
        //   * S1 (targeted Created v1) genuinely starts and remains
        //     pending in transport.
        //   * R2 (global Resolved v2) admits, captures S1's shared
        //     AttentionDeliveryLineage under the lifecycle lock, then
        //     enters WaitForPendingTransportsAsync outside every lock.
        //   * R2's caller cancels while it is still awaiting settlement.
        //     R2 releases only its OWN v2 lease on the lifecycle; S1's
        //     v1 lease remains active.
        //   * The exact R2 retry (same v2) MUST admit and capture S1's
        //     lineage again — the version-blind design rejected this
        //     because `_participants != 0` (S1 still there) and the
        //     dismissal never fired even after S1 succeeded.
        //   * S1 succeeds late. `HasSuccessfulDelivery` flips true on
        //     the shared lineage. R2 retry observes it via its own
        //     capture and fires exactly one dismissal.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var firstTransportStarted = new TaskCompletionSource<NativePushEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTransport = new TaskCompletionSource<NativePushDispatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        int sendCount = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            int callIndex = Interlocked.Increment(ref sendCount);
            sent.Enqueue(envelope);
            if (callIndex == 1)
            {
                // S1: pauses so its attempt stays pending on the shared
                // AttentionDeliveryLineage while R2 captures, cancels,
                // and retries. This is the older-generation participant
                // that used to keep the lifecycle's participant count
                // > 0 and reject the exact R2 retry.
                firstTransportStarted.TrySetResult(envelope);
                return await releaseFirstTransport.Task.WaitAsync(cancellationToken);
            }

            // R2 retry's dismissal, after S1's late success flipped
            // HasSuccessfulDelivery on the captured lineage.
            return NativePushDispatchResult.Delivered();
        });

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });

        int settlementWaitCount = 0;
        var firstSettlementWait = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retrySettlementWait = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sut.OnResolutionSettlementWaitStartedForTests = () =>
        {
            if (Interlocked.Increment(ref settlementWaitCount) == 1)
            {
                firstSettlementWait.TrySetResult();
            }
            else
            {
                retrySettlementWait.TrySetResult();
            }
        };

        DateTime createdAt = new(2026, 7, 15, 4, 0, 0, DateTimeKind.Utc);
        DateTime resolvedAt = createdAt.AddSeconds(1);

        // S1: targeted Created (v1). Enters transport and pauses.
        Task s1 = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await firstTransportStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // R2 first: global Resolved (v2). Different dispatch lane than
        // S1, so it does not queue on S1's Gate; it fences S1's
        // lifecycle synchronously, captures the pending lineage, then
        // parks in WaitForPendingTransportsAsync where the test seam
        // fires — so we are DEFINITELY still holding the v2 lifecycle
        // lease when the cancellation lands.
        using var firstCts = new CancellationTokenSource();
        Task r2First = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt,
            cancellationToken: firstCts.Token);
        await firstSettlementWait.Task.WaitAsync(TimeSpan.FromSeconds(10));

        firstCts.Cancel();
        Func<Task> awaitCancelledFirst = () => r2First;
        await awaitCancelledFirst.Should().ThrowAsync<OperationCanceledException>();

        // R2 retry (same v2, global). On the buggy version-blind design
        // this returns silently (Stale) because S1's v1 still counts
        // against the shared lifecycle. With version-scoped tracking it
        // MUST admit, capture the SAME lineage instance again, and
        // reach a second settlement wait.
        Task r2Retry = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt);
        await retrySettlementWait.Task.WaitAsync(TimeSpan.FromSeconds(10));

        r2Retry.IsCompleted.Should().BeFalse(
            "the exact-version retry must be admitted and waiting for S1's still-pending transport");

        // Release S1 as a late success. The retry's capture holds the
        // SAME lineage instance, so MarkDelivered on S1 flips
        // HasSuccessfulDelivery for the retry and the dismissal fires.
        releaseFirstTransport.TrySetResult(NativePushDispatchResult.Delivered());
        await Task.WhenAll(s1, r2Retry).WaitAsync(TimeSpan.FromSeconds(10));

        sent.Select(envelope => envelope.ChangeKind).Should().Equal(
            AttentionChangeKind.Created,
            AttentionChangeKind.Resolved);
        sent.Count(envelope => envelope.ChangeKind == AttentionChangeKind.Resolved)
            .Should().Be(1,
                "S1's late success must be observed exactly once by the admitted R2 retry");
        Volatile.Read(ref settlementWaitCount).Should().Be(2,
            "both R2 first and R2 retry must actually enter WaitForPendingTransports — under the version-blind design the retry is rejected before it can capture");
    }

    [Fact]
    public async Task DispatchAsync_ResolutionRetryAfterCancelDuringSettlementWait_LateS1FailureSkipsDismissalWithoutFalseSuccess()
    {
        // Complement of the "late S1 success" variant above: the retry
        // must still be admitted and MUST observe S1's LATE FAILURE
        // across the shared lineage, and MUST NOT fire a false
        // dismissal. Proves the version-scoped fix does not paper over
        // the "no successful delivery" branch when it enables the retry
        // to admit.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var firstTransportStarted = new TaskCompletionSource<NativePushEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTransport = new TaskCompletionSource<NativePushDispatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        int sendCount = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            int callIndex = Interlocked.Increment(ref sendCount);
            sent.Enqueue(envelope);
            if (callIndex == 1)
            {
                firstTransportStarted.TrySetResult(envelope);
                return await releaseFirstTransport.Task.WaitAsync(cancellationToken);
            }

            return NativePushDispatchResult.Delivered();
        });

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });

        int settlementWaitCount = 0;
        var firstSettlementWait = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retrySettlementWait = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        sut.OnResolutionSettlementWaitStartedForTests = () =>
        {
            if (Interlocked.Increment(ref settlementWaitCount) == 1)
            {
                firstSettlementWait.TrySetResult();
            }
            else
            {
                retrySettlementWait.TrySetResult();
            }
        };

        DateTime createdAt = new(2026, 7, 15, 4, 5, 0, DateTimeKind.Utc);
        DateTime resolvedAt = createdAt.AddSeconds(1);

        Task s1 = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await firstTransportStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var firstCts = new CancellationTokenSource();
        Task r2First = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt,
            cancellationToken: firstCts.Token);
        await firstSettlementWait.Task.WaitAsync(TimeSpan.FromSeconds(10));

        firstCts.Cancel();
        Func<Task> awaitCancelledFirst = () => r2First;
        await awaitCancelledFirst.Should().ThrowAsync<OperationCanceledException>();

        Task r2Retry = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt);
        await retrySettlementWait.Task.WaitAsync(TimeSpan.FromSeconds(10));

        r2Retry.IsCompleted.Should().BeFalse(
            "the exact-version retry must be admitted and waiting for S1");

        // Release S1 with a LATE failure — the shared lineage keeps
        // HasSuccessfulDelivery = false. The retry MUST benign-skip
        // the dismissal instead of firing a phantom Resolved envelope.
        releaseFirstTransport.TrySetResult(NativePushDispatchResult.Transient("provider_unavailable"));
        await Task.WhenAll(s1, r2Retry).WaitAsync(TimeSpan.FromSeconds(10));

        sent.Select(envelope => envelope.ChangeKind).Should().Equal(
            AttentionChangeKind.Created);
        sent.Count(envelope => envelope.ChangeKind == AttentionChangeKind.Resolved)
            .Should().Be(0,
                "the shared lineage never delivered, so the admitted retry must not fire a false dismissal");
        Volatile.Read(ref settlementWaitCount).Should().Be(2,
            "the retry must still admit and enter WaitForPendingTransports — proving the no-false-success outcome is not just the retry being silently rejected");
    }

    [Fact]
    public async Task DispatchAsync_TargetedDispatchLaneExactRetryAfterCancelOutsideLaneWhileOlderPending_IsAdmittedAndDelivered()
    {
        // The lane now protects only the latest-version decision; all
        // feature-gate/DB/network awaits run outside it. This preserves the
        // original version-scoped admission proof with a realistic outside-
        // lane cancellation:
        //   * S1 targeted Created (v1) remains active while paused in sender
        //     preparation before transport start.
        //   * R2 targeted Updated (v2) admits on the same lane, releases the
        //     lane, then is canceled in its initial async feature-gate read.
        //   * R2's exact-version retry must admit even though S1's v1
        //     participant remains active.
        //   * The retry delivers v2; releasing S1 then vetoes its stale v1
        //     reservation at the lifecycle transport boundary.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        using var gate = new ControllableAsyncGate();
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var s1PreparationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseS1 = new TaskCompletionSource<NativePushDispatchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        int sendCount = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            int callIndex = Interlocked.Increment(ref sendCount);
            if (callIndex == 1)
            {
                s1PreparationStarted.TrySetResult();
                _ = await releaseS1.Task.WaitAsync(cancellationToken);
            }

            if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            sent.Enqueue(envelope);
            return NativePushDispatchResult.Delivered();
        });

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });

        DateTime createdAt = new(2026, 7, 15, 4, 10, 0, DateTimeKind.Utc);
        DateTime updatedAt = createdAt.AddSeconds(1);

        // S1: targeted Created (v1). Remains active while sender preparation pauses.
        Task s1 = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await s1PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        gate.ArmPauseForCallCounts(pauseAfterCall: 4);
        using var firstCts = new CancellationTokenSource();
        Task r2First = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Updated,
            userId,
            occurredAtUtc: updatedAt,
            cancellationToken: firstCts.Token);

        await gate.WaitForPausedAsync().WaitAsync(TimeSpan.FromSeconds(30));
        firstCts.Cancel();
        Func<Task> awaitCancelledFirst = () => r2First;
        await awaitCancelledFirst.Should().ThrowAsync<OperationCanceledException>();
        gate.DisarmPause();
        gate.ReleasePause();

        Task r2Retry = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Updated,
            userId,
            occurredAtUtc: updatedAt);

        await r2Retry.WaitAsync(TimeSpan.FromSeconds(30));

        releaseS1.TrySetResult(NativePushDispatchResult.Delivered());
        await s1.WaitAsync(TimeSpan.FromSeconds(30));

        sent.Select(envelope => envelope.ChangeKind).Should().Equal(AttentionChangeKind.Updated);
        sent.Count(envelope => envelope.ChangeKind == AttentionChangeKind.Updated)
            .Should().Be(1,
                "the exact-version retry must admit while S1 remains active, and S1 must later be vetoed as stale");
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B1_DisabledModeUniqueCreatedAndUpdatedItems_CreateNoDispatchState()
    {
        // #755 Hicks r2 blocker 1: disabled-mode Created / Updated
        // dispatches MUST NOT create any dispatcher-owned state
        // (AttentionDispatchLane, AttentionItemFence, or
        // AttentionLifecycle). The pre-fix design always observed a
        // lane on entry and only short-circuited at DispatchCoreAsync's
        // settings check, so every unique attention item created a
        // fresh AttentionDispatchLane permanently retained until the
        // seven-day retention TTL. Because Disabled is the DEFAULT
        // operator mode, unique attention items therefore accumulated
        // lanes without bound.
        //
        // The fix short-circuits before TryObserveDispatch when the
        // change kind is not Resolved. This test drives many unique
        // ids through both non-Resolved change kinds and asserts every
        // cache is empty AND that neither the sender nor the scope
        // factory was touched.
        var sender = new Mock<INativePushSender>(MockBehavior.Strict);
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        NativePushDispatcher sut = Build(sender.Object, scopes.Object, NativePushMode.Disabled);

        const int uniqueItems = 500;
        for (int i = 0; i < uniqueItems; i++)
        {
            await sut.DispatchAsync($"att-b1-{i}", AttentionChangeKind.Created, targetUserId: null);
            await sut.DispatchAsync($"att-b1-{i}", AttentionChangeKind.Updated, targetUserId: Guid.NewGuid());
        }

        sut.AttentionDispatchLaneCountForTests.Should().Be(0,
            "disabled-mode non-Resolved dispatches must short-circuit BEFORE TryObserveDispatch — otherwise every unique attention item accumulates a lane retained for the seven-day retention TTL");
        sut.AttentionItemFenceCountForTests.Should().Be(0,
            "no item fence is required for a non-Resolved dispatch that never crosses the pre-exit fencing path");
        sut.AttentionLifecycleCountForTests.Should().Be(0,
            "no per-user lifecycle is installed before the disabled early return");
        sender.VerifyNoOtherCalls();
        scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B1_DisabledModeUniqueGlobalResolvedItems_PruneRetiresPastRetentionEntries()
    {
        // #755 Hicks r2 blocker 1: disabled-mode global Resolved
        // dispatches MUST still publish the item-wide tombstone
        // (cross-lane ordering is a correctness invariant preserved
        // across the operator toggle), but MUST NOT leak lanes or
        // fences without bound. Every early return releases the
        // participant lease first, THEN drives bounded PruneCaches, so
        // past-retention entries are retired even when the dispatcher
        // is disabled.
        var sender = new Mock<INativePushSender>(MockBehavior.Strict);
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var timeProvider = new AdvancingTimeProvider(new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc));
        IOptionsMonitor<NativePushSettings> monitor = new StaticOptionsMonitor(
            new NativePushSettings { Mode = NativePushMode.Disabled });
        var sut = new NativePushDispatcher(
            scopes.Object,
            AsTransportAwareForTests(sender.Object),
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance,
            timeProvider);

        const int uniqueItems = 200;
        for (int i = 0; i < uniqueItems; i++)
        {
            await sut.DispatchAsync($"att-b1-global-{i}", AttentionChangeKind.Resolved, targetUserId: null);
        }

        sut.AttentionItemFenceCountForTests.Should().Be(uniqueItems,
            "global Resolved MUST publish its item-wide tombstone even when disabled — the cross-lane ordering invariant survives the operator toggle");
        sut.AttentionDispatchLaneCountForTests.Should().Be(uniqueItems,
            "each unique lane was just observed and cannot yet be retired by the fresh-touch prune pass");
        sut.AttentionLifecycleCountForTests.Should().Be(0,
            "no Created was ever admitted for these items so no per-user lifecycle exists");

        // Advance well past the seven-day AttentionSnapshotTtl and dispatch
        // one final Resolved. The disabled early return releases its own
        // (fresh) participant lease and then runs PruneCaches — the
        // internal 30-second rate limit is unblocked by the time
        // advancement, so the retire pass runs and retires the 200
        // past-retention entries. Only the freshest entry survives.
        timeProvider.Advance(TimeSpan.FromDays(8));
        await sut.DispatchAsync("att-b1-global-post-retention", AttentionChangeKind.Resolved, targetUserId: null);

        sut.AttentionItemFenceCountForTests.Should().Be(1,
            "PruneCaches on the disabled early return must retire every past-retention fence, leaving only the freshly-touched one");
        sut.AttentionDispatchLaneCountForTests.Should().Be(1,
            "PruneCaches must also retire past-retention lanes even under disabled mode");
        sut.AttentionLifecycleCountForTests.Should().Be(0);
        sender.VerifyNoOtherCalls();
        scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B1_DisabledModeUniqueTargetedResolvedItems_PruneRetiresPastRetentionEntries()
    {
        // #755 Hicks r2 blockers 1 + 3: disabled-mode targeted Resolved
        // dispatches MUST still perform pre-exit fencing (advance the
        // target user's lifecycle to Resolved under the item fence
        // lock) so a concurrent Created cannot start transport for an
        // older generation after the disabled dispatch returns. This
        // pre-exit fencing DOES install a fence + a lifecycle for the
        // targeted user, so unbounded disabled traffic would otherwise
        // leak lifecycles too. Prune runs on the disabled early return
        // and retires all past-retention state.
        var sender = new Mock<INativePushSender>(MockBehavior.Strict);
        var scopes = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var timeProvider = new AdvancingTimeProvider(new DateTime(2026, 7, 15, 8, 30, 0, DateTimeKind.Utc));
        IOptionsMonitor<NativePushSettings> monitor = new StaticOptionsMonitor(
            new NativePushSettings { Mode = NativePushMode.Disabled });
        var sut = new NativePushDispatcher(
            scopes.Object,
            AsTransportAwareForTests(sender.Object),
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance,
            timeProvider);

        const int uniqueItems = 200;
        for (int i = 0; i < uniqueItems; i++)
        {
            await sut.DispatchAsync(
                $"att-b1-tgt-{i}",
                AttentionChangeKind.Resolved,
                targetUserId: Guid.NewGuid());
        }

        sut.AttentionItemFenceCountForTests.Should().Be(uniqueItems,
            "targeted Resolved pre-exit fencing (Hicks r2 B3) installs the item fence even when disabled");
        sut.AttentionLifecycleCountForTests.Should().Be(uniqueItems,
            "each targeted Resolved advances its own target's lifecycle to Resolved under the fence lock");
        sut.AttentionDispatchLaneCountForTests.Should().Be(uniqueItems);

        timeProvider.Advance(TimeSpan.FromDays(8));
        await sut.DispatchAsync(
            "att-b1-tgt-post-retention",
            AttentionChangeKind.Resolved,
            targetUserId: Guid.NewGuid());

        sut.AttentionItemFenceCountForTests.Should().Be(1,
            "PruneCaches on the disabled early return must retire past-retention item fences under targeted-mode traffic too");
        sut.AttentionLifecycleCountForTests.Should().Be(1,
            "PruneCaches must retire past-retention lifecycles even when the dispatcher is disabled");
        sut.AttentionDispatchLaneCountForTests.Should().Be(1);
        sender.VerifyNoOtherCalls();
        scopes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B2_KillSwitchFlipsDuringSenderPreparation_TransportStartBoundaryVetoesWithFullRollback()
    {
        // #755 Hicks r2 blocker 2: an administrator's persisted kill
        // switch committed DURING a sender's preparation window (JWT
        // signing, key I/O, payload build) MUST veto the transport
        // start immediately BEFORE the provider request is accepted.
        // The dispatcher-level gate check in SendWithRetriesAsync
        // runs BEFORE preparation and cannot undo an already-sent
        // push; a later gate check runs AFTER the provider call and
        // also cannot undo it. Only re-evaluating the persisted gate
        // at the actual transport-start boundary (inside
        // DispatcherTransportStart.TryStart) closes this window.
        //
        // Interleaving:
        //   * gate.IsEnabled starts true
        //   * S1: targeted Created v1. Sender pauses AFTER
        //     preparation, BEFORE calling TryStart.
        //   * Test flips gate.IsEnabled => false.
        //   * Sender resumes and calls TryStart. The B2 gate re-check
        //     inside TryStart observes disabled and vetoes with full
        //     rollback: reservation reverts to pending state,
        //     dedupe/rate are returned, Attempted is NOT incremented,
        //     no provider call is made.
        //   * Test flips gate.IsEnabled => true and dispatches the
        //     EXACT same version. This new dispatch admits, reaches
        //     the sender (now running its normal path), calls
        //     TryStart, permits, and delivers exactly once. Attempted
        //     goes to 1 (retry only).
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        int gateEnabled = 1;
        var gate = new Mock<IOperatorFeatureGate>();
        gate.Setup(g => g.IsEnabled(OperatorFeature.NativePush))
            .Returns(() => Volatile.Read(ref gateEnabled) == 1);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.NativePush, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Volatile.Read(ref gateEnabled) == 1));
        gate.Setup(g => g.IsEnabledStrictAsync(OperatorFeature.NativePush, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Volatile.Read(ref gateEnabled) == 1));
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var preparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int senderCalls = 0;
        int providerCalls = 0;
        int startVetoed = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            int index = Interlocked.Increment(ref senderCalls);
            if (index == 1)
            {
                // First attempt: pause AFTER preparation so the test can
                // flip the persisted gate during this window.
                preparationEntered.TrySetResult();
                await releasePreparation.Task.WaitAsync(cancellationToken);

                // Sender does its normal pre-transport cancellation
                // check (it is not cancelled here) then hands off to
                // the dispatcher's transport-start boundary. The B2
                // gate re-check inside TryStart is what must veto.
                cancellationToken.ThrowIfCancellationRequested();
                NativePushTransportStartDecision decision = (await transportStart.TryStartAsync(cancellationToken));
                if (!decision.IsPermitted)
                {
                    Interlocked.Increment(ref startVetoed);
                    return NativePushDispatchResult.TransportStartVetoed();
                }

                Interlocked.Increment(ref providerCalls);
                return NativePushDispatchResult.Delivered();
            }

            // Retry attempt: gate is re-enabled, sender permits and delivers.
            if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCalls);
            return NativePushDispatchResult.Delivered();
        });

        using var metrics = new NativePushMetrics();
        long attempted = 0;
        long skippedFeatureDisabled = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, metrics.Attempted)
                    || ReferenceEquals(instrument, metrics.SkippedFeatureDisabled))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                Interlocked.Add(ref attempted, measurement);
            }
            else if (ReferenceEquals(instrument, metrics.SkippedFeatureDisabled))
            {
                Interlocked.Add(ref skippedFeatureDisabled, measurement);
            }
        });
        meterListener.Start();

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                MaxAttempts = 1,
                DedupeWindow = TimeSpan.FromMinutes(5),
                RateLimitPerUser = 1,
                RateLimitWindow = TimeSpan.FromMinutes(5),
            },
            metrics: metrics);
        DateTime occurredAt = new(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc);

        Task first = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAt);
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Administrator commits the emergency disable while the sender
        // is still inside its preparation window.
        Volatile.Write(ref gateEnabled, 0);

        releasePreparation.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        Volatile.Read(ref providerCalls).Should().Be(0,
            "the persisted disable committed during preparation must veto TryStart before any provider call");
        Volatile.Read(ref startVetoed).Should().Be(1,
            "the sender's TryStart must be vetoed exactly once by the B2 gate re-check");
        Volatile.Read(ref attempted).Should().Be(0,
            "Attempted is only incremented from inside a permitted TryStart — a veto must not touch it");
        Volatile.Read(ref skippedFeatureDisabled).Should().BeGreaterThan(0,
            "the veto records SkippedFeatureDisabled for observability");

        // Re-enable and retry the exact same version. Full rollback
        // means dedupe and rate capacity were returned, so the retry
        // admits and delivers.
        Volatile.Write(ref gateEnabled, 1);
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAt).WaitAsync(TimeSpan.FromSeconds(10));

        Volatile.Read(ref providerCalls).Should().Be(1,
            "the exact-version retry must succeed once the gate is re-enabled — dedupe/rate/lifecycle were rolled back");
        Volatile.Read(ref attempted).Should().Be(1,
            "only the retry crossed the actual transport boundary");
        tokens.Verify(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B2_KillSwitchStaysEnabledDuringPreparation_TransportStartBoundaryPermitsNormally()
    {
        // #755 Hicks r2 blocker 2 (symmetric coverage): the B2
        // gate re-check inside TryStart must NOT reject a normal
        // send when the gate stays enabled from admission through
        // the transport-start boundary. Without this test a
        // misimplementation that stubbornly vetoes every TryStart
        // would still make the B2 failure test pass.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        int gateEnabled = 1;
        var gate = new Mock<IOperatorFeatureGate>();
        gate.Setup(g => g.IsEnabled(OperatorFeature.NativePush))
            .Returns(() => Volatile.Read(ref gateEnabled) == 1);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.NativePush, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Volatile.Read(ref gateEnabled) == 1));
        gate.Setup(g => g.IsEnabledStrictAsync(OperatorFeature.NativePush, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Volatile.Read(ref gateEnabled) == 1));
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        int providerCalls = 0;
        var sender = new DelegateTransportSender(async (_, transportStart, cancellationToken) =>
        {
            if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCalls);
            return NativePushDispatchResult.Delivered();
        });

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });

        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: new(2026, 7, 15, 9, 30, 0, DateTimeKind.Utc));

        Volatile.Read(ref providerCalls).Should().Be(1,
            "the gate stayed enabled through the boundary so TryStart must permit the provider call");
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B3_TargetedResolvedFencesTargetLifecycleBeforeDisabledGate_VetoesLaterS1TransportStart()
    {
        // #755 Hicks r2 blocker 3: a targeted Resolved MUST fence
        // the target user's lifecycle synchronously under the item
        // fence lock BEFORE mode / gate / scope / owner-lookup or
        // any other optional/fallible early exit. Otherwise a
        // concurrent Created that already admitted the target's
        // lifecycle can resume after the targeted Resolved returns
        // and start transport for an older generation — sending a
        // push for an alert that has already been resolved for that
        // user.
        //
        // Deterministic interleaving:
        //   * gate initially enabled
        //   * S1: global Created v1. Enters DispatchForOwnerAsync
        //     for the single owner U, observes U's lifecycle at v1,
        //     reaches the sender. Sender pauses BEFORE calling
        //     TryStart so no transport-start has committed yet.
        //   * Test flips gate.IsEnabled => false (representative
        //     fallible early exit for the R2 path).
        //   * R2: targeted Resolved v2 for user U. Under the fix,
        //     PublishTargetedResolvedFence runs synchronously and
        //     advances U's lifecycle to Resolved v2 BEFORE the gate
        //     check. R2 then hits the disabled gate and returns.
        //   * Test flips gate.IsEnabled => true and releases S1.
        //   * S1's sender calls TryStart. TryStart re-checks the
        //     gate (enabled), then TryStartTransport observes
        //     reservation.Version=v1 != _latest=v2 and vetoes.
        //   * Assert: no provider call for S1, exactly one veto,
        //     Attempted stays at zero, no push was emitted for an
        //     already-resolved alert.
        //
        // Under the pre-B3 design, R2 skips fencing entirely
        // because targeted Resolved never touched the item fence.
        // U's lifecycle stays at v1 and S1 resumes to send a
        // phantom Created push.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        int gateEnabled = 1;
        var gate = new Mock<IOperatorFeatureGate>();
        gate.Setup(g => g.IsEnabled(OperatorFeature.NativePush))
            .Returns(() => Volatile.Read(ref gateEnabled) == 1);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.NativePush, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Volatile.Read(ref gateEnabled) == 1));
        gate.Setup(g => g.IsEnabledStrictAsync(OperatorFeature.NativePush, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Volatile.Read(ref gateEnabled) == 1));
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var s1BeforeTryStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseS1 = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int senderCalls = 0;
        int providerCalls = 0;
        int startVetoed = 0;
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            int index = Interlocked.Increment(ref senderCalls);
            if (index == 1)
            {
                // S1: pause BEFORE crossing the transport boundary.
                s1BeforeTryStart.TrySetResult();
                await releaseS1.Task.WaitAsync(cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
                {
                    Interlocked.Increment(ref startVetoed);
                    return NativePushDispatchResult.TransportStartVetoed();
                }

                Interlocked.Increment(ref providerCalls);
                sent.Enqueue(envelope);
                return NativePushDispatchResult.Delivered();
            }

            // R2 never reaches the sender in the fenced-then-disabled
            // path because DispatchCoreAsync bails at the gate check
            // before DispatchForOwnerAsync. Any call here would be
            // unexpected.
            Interlocked.Increment(ref providerCalls);
            sent.Enqueue(envelope);
            return NativePushDispatchResult.Delivered();
        });

        using var metrics = new NativePushMetrics();
        long attempted = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, metrics.Attempted))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                Interlocked.Add(ref attempted, measurement);
            }
        });
        meterListener.Start();

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 },
            metrics: metrics);

        DateTime createdAt = new(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc);
        DateTime resolvedAt = createdAt.AddSeconds(1);

        // S1: global Created v1. Enumerates the single active owner
        // (U) and pauses inside DispatchForOwnerAsync's sender call.
        Task s1 = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            targetUserId: null,
            occurredAtUtc: createdAt);
        await s1BeforeTryStart.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Flip the gate to disabled — R2's fallible early exit is
        // the persisted-gate check inside DispatchCoreAsync.
        Volatile.Write(ref gateEnabled, 0);

        // R2: targeted Resolved v2 for U. With B3 the pre-exit fence
        // advances U's lifecycle to Resolved v2 synchronously under
        // the item fence lock, THEN DispatchCoreAsync hits the
        // disabled gate check and returns without dispatching.
        Task r2 = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: userId,
            occurredAtUtc: resolvedAt);
        await r2.WaitAsync(TimeSpan.FromSeconds(10));

        // Re-enable and release S1.
        Volatile.Write(ref gateEnabled, 1);
        releaseS1.TrySetResult();
        await s1.WaitAsync(TimeSpan.FromSeconds(10));

        Volatile.Read(ref providerCalls).Should().Be(0,
            "R2's pre-exit fence advances U's lifecycle to Resolved v2 BEFORE hitting the disabled gate, so S1's TryStart at v1 must veto at TryStartTransport (v1 != v2)");
        Volatile.Read(ref startVetoed).Should().Be(1,
            "S1 must observe the version-based veto exactly once");
        Volatile.Read(ref attempted).Should().Be(0,
            "Attempted is only incremented from a permitted TryStart; the veto must leave it untouched");
        sent.Should().BeEmpty("no push must be emitted for an alert that R2 has already resolved for U");
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B3_TargetedResolvedFencesTargetLifecycleBeforeScopeCreationFailure_VetoesLaterS1TransportStart()
    {
        // #755 Hicks r2 blocker 3 (fallible-early-exit variant): a
        // second representative early exit — a scope-creation failure
        // that surfaces before DispatchCoreAsync can enumerate owners
        // — must not defeat the pre-exit targeted fence either. Same
        // interleaving as the disabled-gate variant but the R2
        // dispatch runs against a scope factory that throws on
        // creation. The pre-exit fence completes synchronously
        // BEFORE DispatchCoreAsync's scope open call, so U's
        // lifecycle still advances to Resolved v2. Verifies the fix
        // does not depend on any specific early-exit reason: as long
        // as the fence publishes BEFORE the fallible work, S1 is
        // fenced.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var s1BeforeTryStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseS1 = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int senderCalls = 0;
        int providerCalls = 0;
        int startVetoed = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            int index = Interlocked.Increment(ref senderCalls);
            if (index == 1)
            {
                s1BeforeTryStart.TrySetResult();
                await releaseS1.Task.WaitAsync(cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (!(await transportStart.TryStartAsync(cancellationToken)).IsPermitted)
                {
                    Interlocked.Increment(ref startVetoed);
                    return NativePushDispatchResult.TransportStartVetoed();
                }

                Interlocked.Increment(ref providerCalls);
                return NativePushDispatchResult.Delivered();
            }

            Interlocked.Increment(ref providerCalls);
            return NativePushDispatchResult.Delivered();
        });

        // Build the dispatcher with a scope factory whose SECOND call
        // (used by R2 after S1 has already opened its scope) throws.
        // The pre-exit targeted fence completes before this throw is
        // observed.
        var services = new ServiceCollection();
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton(attention.Object);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        var toggleScope = new ToggleFailingServiceScopeFactory(
            provider.GetRequiredService<IServiceScopeFactory>());
        IOptionsMonitor<NativePushSettings> monitor = new StaticOptionsMonitor(
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        var sut = new NativePushDispatcher(
            toggleScope,
            AsTransportAwareForTests(sender),
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);

        DateTime createdAt = new(2026, 7, 15, 10, 30, 0, DateTimeKind.Utc);
        DateTime resolvedAt = createdAt.AddSeconds(1);

        Task s1 = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            targetUserId: null,
            occurredAtUtc: createdAt);
        await s1BeforeTryStart.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Arm the next scope creation to fail — this is R2's
        // fallible early exit and happens AFTER the pre-exit
        // targeted fence has already advanced U's lifecycle.
        toggleScope.FailNext = true;

        Task r2 = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: userId,
            occurredAtUtc: resolvedAt);
        await r2.WaitAsync(TimeSpan.FromSeconds(10));

        // The fallible exit did NOT undo the pre-exit fence: U's
        // lifecycle is at Resolved v2, so S1's transport-start
        // vetoes on version mismatch.
        releaseS1.TrySetResult();
        await s1.WaitAsync(TimeSpan.FromSeconds(10));

        Volatile.Read(ref providerCalls).Should().Be(0,
            "R2's pre-exit targeted fence must advance U's lifecycle to Resolved v2 regardless of a downstream fallible early exit — S1 at v1 must veto at TryStartTransport");
        Volatile.Read(ref startVetoed).Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_CancellationBeforeTransportStart_RollsBackDedupeRateAndAttemptMetricForExactRetry()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);
        var preparationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int senderCalls = 0;
        int startedTransports = 0;
        var sender = new DelegateTransportSender(async (_, transportStart, cancellationToken) =>
        {
            if (Interlocked.Increment(ref senderCalls) == 1)
            {
                preparationEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            (await transportStart.TryStartAsync(cancellationToken)).IsPermitted.Should().BeTrue();
            Interlocked.Increment(ref startedTransports);
            return NativePushDispatchResult.Delivered();
        });
        using var metrics = new NativePushMetrics();
        long attempted = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                Interlocked.Add(ref attempted, measurement);
            }
        });
        meterListener.Start();
        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings
            {
                Mode = NativePushMode.Direct,
                MaxAttempts = 1,
                DedupeWindow = TimeSpan.FromMinutes(5),
                RateLimitPerUser = 1,
                RateLimitWindow = TimeSpan.FromMinutes(5),
            },
            metrics: metrics);
        DateTime occurredAt = new(2026, 7, 15, 1, 10, 0, DateTimeKind.Utc);
        using var cts = new CancellationTokenSource();

        Task first = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAt,
            cts.Token);
        await preparationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await first.WaitAsync(TimeSpan.FromSeconds(10)));

        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAt).WaitAsync(TimeSpan.FromSeconds(10));

        senderCalls.Should().Be(2);
        Volatile.Read(ref startedTransports).Should().Be(1);
        Volatile.Read(ref attempted).Should().Be(1,
            "only the retry crossed the actual transport boundary");
        tokens.Verify(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_MisbehavingSenderCallsTryStartAfterCancellation_DispatcherGuardVetoesAndAllowsExactRetry()
    {
        // Hicks blocker 2 (dispatcher-side guard): the two real senders get
        // their own tests proving THEY check cancellation before ever
        // calling TryStart() (RelayNativePushSenderTests /
        // DirectApnsNativePushSenderTests). This test proves the dispatcher
        // does not rely solely on that — a sender that forgets or misorders
        // its own pre-transport cancellation check and calls TryStart()
        // anyway, after the caller's token has already been cancelled, must
        // still be vetoed by DispatcherTransportStart's own token-aware
        // handshake: no provider call, no Attempted increment, reservations
        // rolled back, and the exact version remains retryable.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var readyToCancel = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSender = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int tryStartCalls = 0;
        int providerCalls = 0;
        var sender = new DelegateTransportSender(async (_, transportStart, _) =>
        {
            // Deliberately does NOT check its own cancellation token — this is
            // exactly the "misbehaving sender" the dispatcher-side guard
            // must defend against. It waits on a plain (non-cancellation-
            // aware) signal so the test can force the caller/dispatcher
            // cancellation captured at DispatcherTransportStart construction
            // to already be signalled before this call reaches TryStartAsync().
            //
            // A CancellationToken.None is passed as the sender-side token so
            // the dispatcher's veto path is driven by the caller/dispatcher
            // token, not the sender's own — matching the interface contract
            // that the dispatcher never relies solely on the sender.
            readyToCancel.TrySetResult();
            await releaseSender.Task;
            Interlocked.Increment(ref tryStartCalls);
            if (!(await transportStart.TryStartAsync(CancellationToken.None)).IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCalls);
            return NativePushDispatchResult.Delivered();
        });

        using var metrics = new NativePushMetrics();
        long attempted = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                Interlocked.Add(ref attempted, measurement);
            }
        });
        meterListener.Start();

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 },
            metrics: metrics);
        DateTime occurredAt = new(2026, 7, 15, 2, 0, 0, DateTimeKind.Utc);
        using var cts = new CancellationTokenSource();

        Task first = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAt,
            cts.Token);
        await readyToCancel.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        releaseSender.TrySetResult();

        await first.WaitAsync(TimeSpan.FromSeconds(10));

        Volatile.Read(ref tryStartCalls).Should().Be(1,
            "the misbehaving sender still calls TryStart despite the already-cancelled token");
        Volatile.Read(ref providerCalls).Should().Be(0,
            "the dispatcher-side guard must veto a pre-cancelled attempt even when the sender forgot its own check");
        Volatile.Read(ref attempted).Should().Be(0,
            "Attempted must not increment for a vetoed, never-started transport");

        // Exact-version retry with a fresh (non-cancelled) token must be
        // free to reach the provider boundary — the vetoed attempt rolled
        // back its reservations rather than leaving them committed.
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAt).WaitAsync(TimeSpan.FromSeconds(10));

        Volatile.Read(ref providerCalls).Should().Be(1);
        Volatile.Read(ref attempted).Should().Be(1);
        tokens.Verify(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_RapidFailureAfterTransportStart_CommitsAttemptAndContinuesSiblingDevices()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        DeviceToken firstDevice = MakeToken(userId, "post-start-fault-a");
        DeviceToken secondDevice = MakeToken(userId, "post-start-fault-b");
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(repository => repository.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { userId });
        tokens.Setup(repository => repository.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken> { firstDevice, secondDevice });
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        tokens.Setup(repository => repository.RecordFailureAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);
        var attemptedDevices = new ConcurrentQueue<Guid>();
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            (await transportStart.TryStartAsync(cancellationToken)).IsPermitted.Should().BeTrue();
            Guid deviceId = Guid.Parse(envelope.DeviceTokenId);
            attemptedDevices.Enqueue(deviceId);
            if (deviceId == firstDevice.Id)
            {
                throw new InvalidOperationException("simulated rapid post-start provider fault");
            }

            return NativePushDispatchResult.Delivered();
        });
        using var metrics = new NativePushMetrics();
        long attempted = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                Interlocked.Add(ref attempted, measurement);
            }
        });
        meterListener.Start();
        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 },
            metrics: metrics);
        DateTime occurredAt = new(2026, 7, 15, 1, 15, 0, DateTimeKind.Utc);

        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAt).WaitAsync(TimeSpan.FromSeconds(10));
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAt).WaitAsync(TimeSpan.FromSeconds(10));

        attemptedDevices.Should().Equal(
            new[] { firstDevice.Id, secondDevice.Id },
            "the started fault is committed, but the sibling still receives its own attempt");
        Volatile.Read(ref attempted).Should().Be(2);
        tokens.Verify(repository => repository.RecordSuccessAsync(
                secondDevice.Id,
                secondDevice.RegistrationVersion,
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(GlobalResolutionEarlyExit.DispatcherDisabled)]
    [InlineData(GlobalResolutionEarlyExit.FeatureDisabled)]
    [InlineData(GlobalResolutionEarlyExit.ScopeCreationFailure)]
    [InlineData(GlobalResolutionEarlyExit.ActiveOwnerLookupFailure)]
    public async Task DispatchAsync_GlobalResolvedEarlyExit_FencesOlderTargetedCreatedAfterRecovery(
        GlobalResolutionEarlyExit earlyExit)
    {
        Guid userId = Guid.NewGuid();
        Guid staleUserId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        bool featureEnabled = true;
        var gate = new Mock<IOperatorFeatureGate>();
        gate.Setup(value => value.IsEnabled(OperatorFeature.NativePush))
            .Returns(() => featureEnabled);
        gate.Setup(value => value.IsEnabledAsync(OperatorFeature.NativePush, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(featureEnabled));
        gate.Setup(value => value.IsEnabledStrictAsync(OperatorFeature.NativePush, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(featureEnabled));
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        int activeOwnerLookups = 0;
        tokens.Setup(repository => repository.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (earlyExit == GlobalResolutionEarlyExit.ActiveOwnerLookupFailure
                    && Interlocked.Increment(ref activeOwnerLookups) == 1)
                {
                    return Task.FromException<IReadOnlyList<Guid>>(
                        new InvalidOperationException("simulated active-owner lookup failure"));
                }

                return Task.FromResult<IReadOnlyList<Guid>>(new[] { userId });
            });
        tokens.Setup(repository => repository.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        DeviceToken staleUserToken = MakeToken(staleUserId, "early-exit-stale-recipient");
        tokens.Setup(repository => repository.GetActiveByUserAsync(
                staleUserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken> { staleUserToken });
        var attention = new Mock<IAttentionService>();
        attention.Setup(service => service.FindItemAsync(
                userId,
                item.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        attention.Setup(service => service.FindItemAsync(
                staleUserId,
                item.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        var sent = new ConcurrentQueue<NativePushEnvelope>();
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(value => value.ModeName).Returns("test");
        sender.Setup(value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Callback<NativePushEnvelope, CancellationToken>((envelope, _) => sent.Enqueue(envelope))
            .ReturnsAsync(NativePushDispatchResult.Delivered());
        var services = new ServiceCollection();
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton(attention.Object);
        services.AddSingleton(db);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var faultingScopeFactory = new ToggleFailingServiceScopeFactory(scopeFactory);
        var settings = new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 };
        var monitor = new MutableOptionsMonitor(settings);
        var sut = new NativePushDispatcher(
            faultingScopeFactory,
            AsTransportAwareForTests(sender.Object),
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);
        DateTime createdAt = new(2026, 7, 15, 1, 20, 0, DateTimeKind.Utc);

        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            createdAt).WaitAsync(TimeSpan.FromSeconds(10));
        sent.Select(envelope => envelope.ChangeKind).Should().Equal(AttentionChangeKind.Created);

        switch (earlyExit)
        {
            case GlobalResolutionEarlyExit.DispatcherDisabled:
                settings.Mode = NativePushMode.Disabled;
                break;
            case GlobalResolutionEarlyExit.FeatureDisabled:
                featureEnabled = false;
                break;
            case GlobalResolutionEarlyExit.ScopeCreationFailure:
                faultingScopeFactory.FailNext = true;
                break;
            case GlobalResolutionEarlyExit.ActiveOwnerLookupFailure:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(earlyExit), earlyExit, null);
        }

        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: createdAt.AddSeconds(1)).WaitAsync(TimeSpan.FromSeconds(10));

        settings.Mode = NativePushMode.Direct;
        featureEnabled = true;
        faultingScopeFactory.FailNext = false;
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            staleUserId,
            createdAt).WaitAsync(TimeSpan.FromSeconds(10));

        sent.Select(envelope => envelope.ChangeKind).Should().Equal(
            new[] { AttentionChangeKind.Created },
            "the global resolution tombstone must outlive every skipped or failed delivery path");
        sender.Verify(
            value => value.SendAsync(
                It.IsAny<NativePushEnvelope>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static async Task AssertPostSendDisableDiscardsResultAsync(
        NativePushMode mode,
        NativePushDispatchResult completedResult)
    {
        Guid firstOwner = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid laterOwner = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Guid firstRegistrationId = Guid.Parse("00000000-0000-0000-0000-000000000101");
        Guid sameOwnerRegistrationId = Guid.Parse("00000000-0000-0000-0000-000000000102");
        Guid laterOwnerRegistrationId = Guid.Parse("00000000-0000-0000-0000-000000000201");
        const string installationId = "kill-switch-primary";
        string originalToken = new('a', 64);
        string replacementToken = new('z', 64);
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"native-push-post-send-disable-{Guid.NewGuid():N}.db");
        string connectionString =
            $"Data Source={databasePath};Pooling=False;Default Timeout=5";
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        try
        {
            await using (AppDbContext seed = new(options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Users.AddRange(
                    BuildUser(firstOwner, "post-disable-first"),
                    BuildUser(laterOwner, "post-disable-later"));
                seed.NotificationPreferences.AddRange(
                    BuildPushPreferences(firstOwner),
                    BuildPushPreferences(laterOwner));
                seed.AppSettingsEntities.Add(new AppSettingsEntity
                {
                    Key = OperatorFeatureSettings.SectionName,
                    SettingsJson = JsonSerializer.Serialize(new OperatorFeatureSettings
                    {
                        NativePushEnabled = true,
                    }),
                    UpdatedAt = DateTime.UtcNow,
                });
                seed.DeviceTokens.AddRange(
                    new DeviceToken
                    {
                        Id = firstRegistrationId,
                        UserId = firstOwner,
                        RegistrationVersion = 1,
                        InstallationId = installationId,
                        Token = originalToken,
                        Platform = "ios",
                        Environment = "production",
                        AppBundleId = "com.example.primary",
                        CreatedAt = DateTime.UtcNow,
                        LastUsedAt = DateTime.UtcNow,
                        IsActive = true,
                    },
                    new DeviceToken
                    {
                        Id = sameOwnerRegistrationId,
                        UserId = firstOwner,
                        RegistrationVersion = 1,
                        InstallationId = "kill-switch-same-owner",
                        Token = new string('b', 64),
                        Platform = "ios",
                        Environment = "production",
                        AppBundleId = "com.example.same-owner",
                        CreatedAt = DateTime.UtcNow,
                        LastUsedAt = DateTime.UtcNow,
                        IsActive = true,
                    },
                    new DeviceToken
                    {
                        Id = laterOwnerRegistrationId,
                        UserId = laterOwner,
                        RegistrationVersion = 1,
                        InstallationId = "kill-switch-later-owner",
                        Token = new string('c', 64),
                        Platform = "ios",
                        Environment = "production",
                        AppBundleId = "com.example.later-owner",
                        CreatedAt = DateTime.UtcNow,
                        LastUsedAt = DateTime.UtcNow,
                        IsActive = true,
                    });
                await seed.SaveChangesAsync();
            }

            var attention = new Mock<IAttentionService>();
            attention.Setup(service => service.FindItemAsync(
                    It.IsAny<Guid>(),
                    item.Id,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(item);
            var outcomeProbe = new TokenOutcomeProbe();
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(connectionString));
            services.AddScoped<IAppSettingsRepository, EfAppSettingsRepository>();
            services.AddScoped<IOperatorFeatureGate, OperatorFeatureGate>();
            services.AddSingleton(outcomeProbe);
            services.AddScoped<IDeviceTokenRepository>(provider =>
                new OutcomeRecordingDeviceTokenRepository(
                    new EfDeviceTokenRepository(provider.GetRequiredService<AppDbContext>()),
                    provider.GetRequiredService<TokenOutcomeProbe>()));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton<ILogger<OperatorFeatureGate>>(
                NullLogger<OperatorFeatureGate>.Instance);
            services.AddSingleton<IAttentionService>(attention.Object);
            await using ServiceProvider provider = services.BuildServiceProvider();

            var sendStarted = new TaskCompletionSource<NativePushEnvelope>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSend = new TaskCompletionSource<NativePushDispatchResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = new ConcurrentQueue<NativePushEnvelope>();
            var sender = new Mock<INativePushSender>();
            sender.SetupGet(value => value.ModeName).Returns(mode.ToString().ToLowerInvariant());
            sender.Setup(value => value.SendAsync(
                    It.IsAny<NativePushEnvelope>(),
                    It.IsAny<CancellationToken>()))
                .Returns<NativePushEnvelope, CancellationToken>(async (envelope, cancellationToken) =>
                {
                    sent.Enqueue(envelope);
                    if (sent.Count == 1)
                    {
                        sendStarted.TrySetResult(envelope);
                        return await releaseSend.Task.WaitAsync(cancellationToken);
                    }

                    return NativePushDispatchResult.Delivered();
                });

            using var metrics = new NativePushMetrics();
            long attempted = 0;
            long disabled = 0;
            long delivered = 0;
            long invalidated = 0;
            long terminalFailed = 0;
            long transientFailed = 0;
            using var meterListener = new MeterListener();
            meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, metrics.Attempted)
                    || ReferenceEquals(instrument, metrics.SkippedFeatureDisabled)
                    || ReferenceEquals(instrument, metrics.Delivered)
                    || ReferenceEquals(instrument, metrics.TokensInvalidated)
                    || ReferenceEquals(instrument, metrics.TerminalFailed)
                    || ReferenceEquals(instrument, metrics.TransientFailed))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                if (ReferenceEquals(instrument, metrics.Attempted))
                {
                    Interlocked.Add(ref attempted, measurement);
                }
                else if (ReferenceEquals(instrument, metrics.SkippedFeatureDisabled))
                {
                    Interlocked.Add(ref disabled, measurement);
                }
                else if (ReferenceEquals(instrument, metrics.Delivered))
                {
                    Interlocked.Add(ref delivered, measurement);
                }
                else if (ReferenceEquals(instrument, metrics.TokensInvalidated))
                {
                    Interlocked.Add(ref invalidated, measurement);
                }
                else if (ReferenceEquals(instrument, metrics.TerminalFailed))
                {
                    Interlocked.Add(ref terminalFailed, measurement);
                }
                else if (ReferenceEquals(instrument, metrics.TransientFailed))
                {
                    Interlocked.Add(ref transientFailed, measurement);
                }
            });
            meterListener.Start();

            using var sut = new NativePushDispatcher(
                provider.GetRequiredService<IServiceScopeFactory>(),
                AsTransportAwareForTests(sender.Object),
                new StaticOptionsMonitor(new NativePushSettings
                {
                    Mode = mode,
                    MaxAttempts = 3,
                    FailureDeactivationThreshold = 1,
                }),
                metrics,
                NullLogger<NativePushDispatcher>.Instance);
            Task dispatch = sut.DispatchAsync(
                item.Id,
                AttentionChangeKind.Created,
                targetUserId: null);
            try
            {
                NativePushEnvelope inFlight = await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                inFlight.DeviceTokenId.Should().Be(firstRegistrationId.ToString("D"));
                inFlight.Token.Should().Be(originalToken);

                await using (AppDbContext mutate = new(options))
                {
                    DeviceToken replacement = await new EfDeviceTokenRepository(mutate).UpsertAsync(
                        firstOwner,
                        installationId,
                        replacementToken,
                        "ios",
                        "development",
                        "com.example.replacement");
                    replacement.RegistrationVersion.Should().Be(2);

                    AppSettingsEntity settingsRow = await mutate.AppSettingsEntities
                        .SingleAsync(row => row.Key == OperatorFeatureSettings.SectionName);
                    settingsRow.SettingsJson = JsonSerializer.Serialize(new OperatorFeatureSettings
                    {
                        NativePushEnabled = false,
                    });
                    settingsRow.UpdatedAt = DateTime.UtcNow;
                    await mutate.SaveChangesAsync();
                }

                DeviceToken replacementBaseline;
                await using (AppDbContext baseline = new(options))
                {
                    replacementBaseline = await baseline.DeviceTokens
                        .AsNoTracking()
                        .SingleAsync(token => token.Id == firstRegistrationId);
                }

                releaseSend.TrySetResult(completedResult);
                await dispatch.WaitAsync(TimeSpan.FromSeconds(10));

                sent.Should().ContainSingle(
                    "a post-send disable must stop retries, later devices, and later owners");
                outcomeProbe.SuccessWrites.Should().Be(0);
                outcomeProbe.FailureWrites.Should().Be(0);
                outcomeProbe.InvalidationWrites.Should().Be(0);
                Volatile.Read(ref attempted).Should().Be(1);
                Volatile.Read(ref disabled).Should().BeGreaterThan(
                    0,
                    "the completed result must be reported as stopped by the persisted kill switch");
                Volatile.Read(ref delivered).Should().Be(0);
                Volatile.Read(ref invalidated).Should().Be(0);
                Volatile.Read(ref terminalFailed).Should().Be(0);
                Volatile.Read(ref transientFailed).Should().Be(0);

                await using AppDbContext verify = new(options);
                DeviceToken[] persisted = await verify.DeviceTokens
                    .AsNoTracking()
                    .OrderBy(token => token.Id)
                    .ToArrayAsync();
                persisted.Should().HaveCount(3);
                DeviceToken replacementAfter = persisted.Single(token => token.Id == firstRegistrationId);
                replacementAfter.RegistrationVersion.Should().Be(replacementBaseline.RegistrationVersion);
                replacementAfter.Token.Should().Be(replacementBaseline.Token);
                replacementAfter.Environment.Should().Be(replacementBaseline.Environment);
                replacementAfter.AppBundleId.Should().Be(replacementBaseline.AppBundleId);
                replacementAfter.LastUsedAt.Should().Be(replacementBaseline.LastUsedAt);
                replacementAfter.LastFailureAt.Should().Be(replacementBaseline.LastFailureAt);
                replacementAfter.ConsecutiveFailureCount.Should().Be(replacementBaseline.ConsecutiveFailureCount);
                replacementAfter.IsActive.Should().BeTrue();
                persisted.Should().OnlyContain(token =>
                    token.ConsecutiveFailureCount == 0
                    && token.LastFailureAt == null
                    && token.IsActive);
            }
            finally
            {
                releaseSend.TrySetResult(NativePushDispatchResult.Transient("test-cleanup"));
                await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    // -----------------------------------------------------------------------
    // Hicks r2 blocker 1 tests — resolution fencing outside a safe options
    // boundary. Reads of IOptionsMonitor<NativePushSettings>.CurrentValue can
    // throw during validated configuration reload, so any read taken BEFORE
    // resolution fencing (or between fence acquisition and the top-level
    // try/finally that owns lease release) can (a) let an older prepared send
    // start unfenced, or (b) leak fence leases and stale exact-version retry
    // reservations.
    //
    // The dispatcher's contract:
    //   * For Resolved (global or targeted), publish/fence synchronously
    //     BEFORE every fallible options/gate/scope/owner/delivery op.
    //   * Immediately establish a top-level try/finally that owns and
    //     releases fence/lifecycle leases. Every post-fence fallible op
    //     runs inside it.
    //   * For non-Resolved, options/config failures must happen BEFORE
    //     any persistent state (lane, fence, lifecycle) is allocated.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DispatchAsync_HicksR2_B1_OptionsThrowDuringGlobalResolved_FencesLifecycleAndVetoesOlderS1_AndAdmitsExactRetryAfterRecovery()
    {
        // Deterministic interleaving:
        //   * S1 (Created v1): sender pauses AFTER preparation, before
        //     invoking transportStart.TryStartAsync. Lifecycle installed
        //     at v1; sender is holding a valid reservation.
        //   * Test arms the options monitor to throw on the NEXT single
        //     CurrentValue read (simulating a validated-options reload
        //     throw). All subsequent reads succeed.
        //   * R2 (Resolved v2 global): DispatchAsync entry MUST NOT read
        //     options for Resolved kinds (blocker 1 A). DispatchCoreAsync
        //     publishes the tombstone + advances U's lifecycle to v2 under
        //     the AttentionItemFence lock BEFORE reading options. Options
        //     read then throws; the top-level try/finally releases the fence
        //     lease and PruneCaches drives bounded reclamation using
        //     defaults.
        //   * Test releases S1. Sender calls TryStartAsync — dispatcher's
        //     async gate returns true, then TryStartTransport observes
        //     reservation.Version=v1 != _latest=v2 and vetoes S1.
        //   * Test dispatches R2 as an exact-version retry (same v2). The
        //     options monitor has recovered so the retry proceeds through
        //     the fence, admits the resolution, and completes.
        //
        // Assertions: providerCalls stays at 0 for the entire test
        // (Resolved carries the Background-priority dismiss which is a
        // silent push — the delivery still fires under the retry, but the
        // key invariant is S1 never delivers a phantom Created push after
        // R2 fenced it). Metrics: Attempted stays 0 for S1's veto, then
        // increments on R2's retry attempt.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var s1PreparationDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int senderCalls = 0;
        int providerCallsS1 = 0;
        int providerCallsRetry = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            int idx = Interlocked.Increment(ref senderCalls);
            if (envelope.ChangeKind == AttentionChangeKind.Created)
            {
                s1PreparationDone.TrySetResult();
                await s1Release.Task.WaitAsync(cancellationToken);
                NativePushTransportStartDecision decision = await transportStart.TryStartAsync(cancellationToken);
                if (!decision.IsPermitted)
                {
                    return NativePushDispatchResult.TransportStartVetoed();
                }

                Interlocked.Increment(ref providerCallsS1);
                return NativePushDispatchResult.Delivered();
            }

            NativePushTransportStartDecision d = await transportStart.TryStartAsync(cancellationToken);
            if (!d.IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCallsRetry);
            return NativePushDispatchResult.Delivered();
        });

        using var throwingMonitor = new ThrowingOptionsMonitor(
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        using var metrics = new NativePushMetrics();
        long attempted = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, metrics.Attempted))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                Interlocked.Add(ref attempted, measurement);
            }
        });
        meterListener.Start();

        var services = new ServiceCollection();
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton(attention.Object);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        using var sut = new NativePushDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            AsTransportAwareForTests(sender),
            throwingMonitor,
            metrics,
            NullLogger<NativePushDispatcher>.Instance);
        DateTime createdAt = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

        // S1: Created v1 — sender pauses inside.
        Task s1 = sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: createdAt);
        await s1PreparationDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Arm the monitor to throw on the very next CurrentValue read only.
        // R2's DispatchCoreAsync will invoke it AFTER the fence has been
        // published; the top-level try/finally must therefore release the
        // fence lease cleanly even though the options read throws.
        throwingMonitor.ArmThrowsForNextReads(1);
        DateTime resolvedAt = createdAt.AddSeconds(1);
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt);

        // Release S1. Its sender resumes and calls TryStartAsync. The gate is
        // enabled, but the lifecycle has been advanced to v2 by R2's fence
        // so TryStartTransport vetoes S1's v1 reservation.
        s1Release.TrySetResult();
        await s1.WaitAsync(TimeSpan.FromSeconds(10));

        Volatile.Read(ref providerCallsS1).Should().Be(0,
            "S1 is fenced by R2's synchronous lifecycle advancement inside the AttentionItemFence lock; " +
            "the options throw does not undo that fencing, so S1's TryStartAsync must veto even though the gate is enabled");

        // Exact retry of R2 (same version). The monitor has recovered so
        // this retry proceeds through the fence. The lane admits the
        // exact-version retry because the first R2 completed its lane
        // participant during its own DispatchAsync finally. S1's paused
        // transport never committed lifecycle ownership (no successful
        // delivery on the underlying occurrence), so the retry lands in
        // the "resolutionCapture == null" path after attention lookup and
        // does NOT emit a silent dismissal — the important observation for
        // this test is that the retry PROCEEDED THROUGH THE FENCE to the
        // attention lookup, proving no leaked fence/lifecycle lease.
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt);

        attention.Verify(
            a => a.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()),
            Times.AtLeast(2),
            "S1's Created called attention.FindItemAsync once (before its transport was fenced), and R2's exact-version retry must call it again after proceeding through the fence — proving the fence lease was released cleanly and the retry was admitted");
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B1_OptionsThrowDuringTargetedResolved_FencesTargetLifecycleAndVetoesOlderS1_AndAdmitsExactRetryAfterRecovery()
    {
        // Symmetric to the global-resolution test above, exercising
        // <see cref="PublishTargetedResolvedFence"/>'s ordering-fence path.
        // Under the fix, the targeted Resolved's item fence acquisition
        // and per-user lifecycle advance happen synchronously in
        // DispatchCoreAsync BEFORE the options read, so an options throw
        // still leaves the target's lifecycle at Resolved v2 and vetoes
        // any concurrent older-version transport-start.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var s1PreparationDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s1Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int providerCallsS1 = 0;
        int providerCallsRetry = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            if (envelope.ChangeKind == AttentionChangeKind.Created)
            {
                s1PreparationDone.TrySetResult();
                await s1Release.Task.WaitAsync(cancellationToken);
                NativePushTransportStartDecision decision = await transportStart.TryStartAsync(cancellationToken);
                if (!decision.IsPermitted)
                {
                    return NativePushDispatchResult.TransportStartVetoed();
                }

                Interlocked.Increment(ref providerCallsS1);
                return NativePushDispatchResult.Delivered();
            }

            NativePushTransportStartDecision d = await transportStart.TryStartAsync(cancellationToken);
            if (!d.IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCallsRetry);
            return NativePushDispatchResult.Delivered();
        });

        using var throwingMonitor = new ThrowingOptionsMonitor(
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });

        var services = new ServiceCollection();
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton(attention.Object);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        using var sut = new NativePushDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            AsTransportAwareForTests(sender),
            throwingMonitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);
        DateTime createdAt = new(2026, 7, 15, 12, 30, 0, DateTimeKind.Utc);

        Task s1 = sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null, occurredAtUtc: createdAt);
        await s1PreparationDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        throwingMonitor.ArmThrowsForNextReads(1);
        DateTime resolvedAt = createdAt.AddSeconds(1);
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Resolved, targetUserId: userId, occurredAtUtc: resolvedAt);

        s1Release.TrySetResult();
        await s1.WaitAsync(TimeSpan.FromSeconds(10));

        Volatile.Read(ref providerCallsS1).Should().Be(0,
            "targeted Resolved's fence advances only the target user's lifecycle to v2 — S1 at v1 must veto");

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Resolved, targetUserId: userId, occurredAtUtc: resolvedAt);

        attention.Verify(
            a => a.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()),
            Times.AtLeast(2),
            "S1's Created called attention.FindItemAsync once, and R2's exact-version retry must call it again after proceeding through the targeted fence — proving the fence lease was released cleanly");
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B1_OptionsThrowAtEntryForNonResolved_ZeroStateAllocation()
    {
        // For non-Resolved change kinds, the disabled-fast-path options read
        // happens BEFORE TryObserveDispatch. If it throws, no dispatch lane,
        // no item fence, and no lifecycle should be allocated: the exception
        // propagates up unchanged (the caller — AttentionBroadcaster — treats
        // dispatcher throws as a delivery-only failure and does not block the
        // attention broadcast).
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var sender = new DelegateTransportSender((_, transportStart, _) =>
            throw new InvalidOperationException("sender must never be reached when options throw at entry"));

        using var throwingMonitor = new ThrowingOptionsMonitor(
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });

        var services = new ServiceCollection();
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton(attention.Object);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        using var sut = new NativePushDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            AsTransportAwareForTests(sender),
            throwingMonitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);

        throwingMonitor.ArmThrowsForNextReads(1);
        Func<Task> act = () => sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: new(2026, 7, 15, 13, 0, 0, DateTimeKind.Utc));

        await act.Should().ThrowAsync<InvalidOperationException>(
            "options throws at the pre-allocation entry read must propagate — no state has been allocated to protect");

        sut.AttentionDispatchLaneCountForTests.Should().Be(0,
            "a throwing entry options read for non-Resolved must not allocate any lane");
        sut.AttentionItemFenceCountForTests.Should().Be(0,
            "a throwing entry options read for non-Resolved must not allocate any item fence");
        sut.AttentionLifecycleCountForTests.Should().Be(0,
            "a throwing entry options read for non-Resolved must not allocate any lifecycle");
    }

    // -----------------------------------------------------------------------
    // Hicks r2 blocker 2 tests — uncancellable sync-over-async persisted gate
    // I/O under lock. The transport-start handshake used to hold a lock while
    // performing sync-over-async EF I/O for the persisted gate re-check.
    // Under the fix, the async gate read runs OUTSIDE every dispatcher/
    // lifecycle/item/transport lock, is cancellation-aware, and fails
    // closed on repository/DB errors with a logged rollback.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task DispatchAsync_HicksR2_B2_AsyncGatePausedAfterSenderPrep_DisableCommittedBeforeReadCompletes_VetoesAndAdmitsExactRetryAfterReenable()
    {
        // Deterministic:
        //   * gate initially enabled; async read paused via ControllableAsyncGate.
        //   * Sender prepares, then calls TryStartAsync.
        //   * Dispatcher's TryStartAsync awaits gate.IsEnabledStrictAsync — the fake
        //     stalls on a TCS held by the test.
        //   * Test flips SetEnabled(false) then Release()s the paused gate.
        //   * Paused gate completes returning false — TryStartAsync vetoes,
        //     rolls back the reservation, does NOT call TryStartTransport,
        //     Attempted stays 0.
        //   * Test flips SetEnabled(true) and dispatches an exact-version
        //     retry — succeeds all the way through.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        using var gate = new ControllableAsyncGate();
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        int providerCalls = 0;
        int vetoedCount = 0;
        var sender = new DelegateTransportSender(async (_, transportStart, cancellationToken) =>
        {
            NativePushTransportStartDecision decision = await transportStart.TryStartAsync(cancellationToken);
            if (!decision.IsPermitted)
            {
                Interlocked.Increment(ref vetoedCount);
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCalls);
            return NativePushDispatchResult.Delivered();
        });

        using var metrics = new NativePushMetrics();
        long attempted = 0;
        long skippedFeatureDisabled = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, metrics.Attempted)
                    || ReferenceEquals(instrument, metrics.SkippedFeatureDisabled))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                Interlocked.Add(ref attempted, measurement);
            }
            else if (ReferenceEquals(instrument, metrics.SkippedFeatureDisabled))
            {
                Interlocked.Add(ref skippedFeatureDisabled, measurement);
            }
        });
        meterListener.Start();

        // Ensure DispatchForOwnerAsync's first async gate reads pass through
        // enabled so the sender is actually invoked. Only the transport-start
        // gate read is paused deterministically.
        gate.SetEnabled(true);
        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 },
            metrics: metrics);

        gate.ArmPauseForCallCounts(pauseAfterCall: 4); // Owner/device/retry gate reads run through (calls 1-4); transport-start gate read (call 5) pauses.
        DateTime occurredAt = new(2026, 7, 15, 14, 0, 0, DateTimeKind.Utc);
        Task dispatch = sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);

        await gate.WaitForPausedAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // The gate read is paused. Flip disabled and then release the pause;
        // the async read resolves to `false` and the dispatcher vetoes.
        gate.SetEnabled(false);
        gate.ReleasePause();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(10));

        Volatile.Read(ref providerCalls).Should().Be(0,
            "the async gate resolved to disabled at the transport-start authorization linearization point");
        Volatile.Read(ref vetoedCount).Should().Be(1);
        Volatile.Read(ref attempted).Should().Be(0,
            "Attempted is only incremented from a permitted TryStartAsync; the veto must leave it untouched");
        Volatile.Read(ref skippedFeatureDisabled).Should().BeGreaterThan(0,
            "the disabled gate at transport-start must be reported as a feature-disabled skip");

        // Re-enable and exact-version retry: delivers once. The fence must
        // not have leaked a lifecycle version reservation.
        gate.SetEnabled(true);
        gate.DisarmPause();
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);
        Volatile.Read(ref providerCalls).Should().Be(1,
            "exact-version retry after re-enable delivers once");
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B2_SlowAsyncGate_CancellationPropagatesPromptly_NoProviderNoAttempted_ExactRetryRecoverable()
    {
        // Slow gate + cancellation must veto promptly and roll back. The
        // caller/dispatcher cancellation token is captured at
        // DispatcherTransportStart construction and linked to the sender's
        // token, so awaiting a paused gate propagates cancellation without
        // waiting on the DB.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        using var gate = new ControllableAsyncGate();
        gate.SetEnabled(true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        int providerCalls = 0;
        var sender = new DelegateTransportSender(async (_, transportStart, cancellationToken) =>
        {
            NativePushTransportStartDecision decision = await transportStart.TryStartAsync(cancellationToken);
            if (!decision.IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCalls);
            return NativePushDispatchResult.Delivered();
        });

        using var metrics = new NativePushMetrics();
        long attempted = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, metrics.Attempted))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                Interlocked.Add(ref attempted, measurement);
            }
        });
        meterListener.Start();

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 },
            metrics: metrics);

        gate.ArmPauseForCallCounts(pauseAfterCall: 4);
        using var cts = new CancellationTokenSource();
        DateTime occurredAt = new(2026, 7, 15, 15, 0, 0, DateTimeKind.Utc);
        Task dispatch = sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt, cancellationToken: cts.Token);

        await gate.WaitForPausedAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Cancel while the async gate is paused. The dispatcher must observe
        // cancellation without waiting for the gate to resolve.
        cts.Cancel();
        Func<Task> act = () => dispatch.WaitAsync(TimeSpan.FromSeconds(10));
        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancellation observed while awaiting the async gate propagates promptly");

        // The gate is still paused (we deliberately never released it) —
        // release it now so the dispatcher's task fully unwinds and no
        // resources are leaked. The dispatcher's own veto path should not
        // depend on this release; cancellation already unwound the await.
        gate.ReleasePause();

        Volatile.Read(ref providerCalls).Should().Be(0);
        Volatile.Read(ref attempted).Should().Be(0);

        // Recovery: dispatch again with a fresh, uncancelled token and same
        // version. Exact-version retry succeeds.
        gate.DisarmPause();
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);
        Volatile.Read(ref providerCalls).Should().Be(1,
            "exact-version retry after cancellation recovery delivers once");
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B2_AsyncGateThrowsDbError_FailsClosedLogsAndRollsBack_ExactRetryRecoverable()
    {
        // Repository/DB errors at the transport-start authorization
        // linearization point must fail closed: no provider call, no
        // Attempted, reservations rolled back, and an explicit log line
        // captured. An exact-version retry after recovery delivers once.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        using var gate = new ControllableAsyncGate();
        gate.SetEnabled(true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        int providerCalls = 0;
        int vetoedCount = 0;
        var sender = new DelegateTransportSender(async (_, transportStart, cancellationToken) =>
        {
            NativePushTransportStartDecision decision = await transportStart.TryStartAsync(cancellationToken);
            if (!decision.IsPermitted)
            {
                Interlocked.Increment(ref vetoedCount);
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCalls);
            return NativePushDispatchResult.Delivered();
        });

        using var metrics = new NativePushMetrics();
        long attempted = 0;
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, metrics.Attempted))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (ReferenceEquals(instrument, metrics.Attempted))
            {
                Interlocked.Add(ref attempted, measurement);
            }
        });
        meterListener.Start();

        var recordingLogger = new RecordingDispatcherLogger();
        var services = new ServiceCollection();
        services.AddSingleton<IOperatorFeatureGate>(gate);
        services.AddSingleton(tokens.Object);
        services.AddSingleton(attention.Object);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        using var sut = new NativePushDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            AsTransportAwareForTests(sender),
            new StaticOptionsMonitor(new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 }),
            metrics,
            recordingLogger);

        gate.ArmThrowOnCall(callIndex: 5, ex: new InvalidOperationException("simulated DB outage at transport-start gate read"));
        DateTime occurredAt = new(2026, 7, 15, 16, 0, 0, DateTimeKind.Utc);
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);

        Volatile.Read(ref providerCalls).Should().Be(0,
            "gate throws must fail closed — no provider call may be admitted");
        Volatile.Read(ref vetoedCount).Should().Be(1);
        Volatile.Read(ref attempted).Should().Be(0);
        recordingLogger.Warnings.Should().Contain(msg =>
            msg.Contains("[NativePush] Feature-gate read failed at transport-start", StringComparison.Ordinal),
            "the dispatcher must log the fail-closed veto with delivery/attention-item context");

        // Recovery: exact-version retry delivers once.
        gate.DisarmThrow();
        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, userId, occurredAtUtc: occurredAt);
        Volatile.Read(ref providerCalls).Should().Be(1,
            "exact-version retry after DB recovery delivers once — no leaked reservation");
    }

    [Fact]
    public async Task DispatchAsync_HicksR2_B2_AsyncGateReadOutsideLock_ConcurrentIndependentTransportNotBlocked()
    {
        // Blocker 2 core claim: the persisted feature-gate read runs OUTSIDE
        // every dispatcher/lifecycle/item/transport lock. Prove it by
        // pausing item A's gate at transport-start while item B (an
        // unrelated attention item for a different user) dispatches
        // concurrently. Item B must reach its provider without waiting
        // on item A's paused gate.
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();
        AttentionItemDto itemA = BuildAttentionItem(AttentionKind.Offline);
        AttentionItemDto itemB = BuildAttentionItem(AttentionKind.Failure);
        await using AppDbContext db = BuildDbContext();
        using var gate = new ControllableAsyncGate();
        gate.SetEnabled(true);
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens.Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { userA, userB });
        tokens.Setup(r => r.GetActiveByUserAsync(userA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    UserId = userA,
                    InstallationId = "install-A",
                    Token = "AA".PadRight(64, 'A'),
                    Platform = "ios",
                    Environment = "development",
                    IsActive = true,
                },
            });
        tokens.Setup(r => r.GetActiveByUserAsync(userB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    UserId = userB,
                    InstallationId = "install-B",
                    Token = "BB".PadRight(64, 'B'),
                    Platform = "ios",
                    Environment = "development",
                    IsActive = true,
                },
            });
        tokens.Setup(r => r.RecordSuccessAsync(
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<DateTime>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var attention = new Mock<IAttentionService>();
        attention.Setup(a => a.FindItemAsync(userA, itemA.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(itemA);
        attention.Setup(a => a.FindItemAsync(userB, itemB.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(itemB);

        int providerA = 0;
        int providerB = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            NativePushTransportStartDecision decision = await transportStart.TryStartAsync(cancellationToken);
            if (!decision.IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            if (envelope.AttentionItemId == itemA.Id)
            {
                Interlocked.Increment(ref providerA);
            }
            else if (envelope.AttentionItemId == itemB.Id)
            {
                Interlocked.Increment(ref providerB);
            }

            return NativePushDispatchResult.Delivered();
        });

        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });

        // Arm the pause so the FIRST transport-start gate read pauses. For a
        // fresh dispatch with 1 owner + 1 device + 1 attempt, the gate is
        // consumed 4 times (dispatch-core initial, per-owner, per-device,
        // retry-loop) BEFORE the transport-start invocation reads the gate
        // as call 5. Pausing at call 5 targets the transport-start read
        // deterministically.
        gate.ArmPauseForCallCounts(pauseAfterCall: 4);
        DateTime occurredAt = new(2026, 7, 15, 17, 0, 0, DateTimeKind.Utc);
        Task dispatchA = sut.DispatchAsync(itemA.Id, AttentionChangeKind.Created, userA, occurredAtUtc: occurredAt);
        await gate.WaitForPausedAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Item A's transport-start is paused inside the async gate read.
        // Item B for a different user/item must not be blocked — its own
        // gate reads run concurrently on a separate lifecycle/lane.
        gate.DisarmPause();
        Task dispatchB = sut.DispatchAsync(itemB.Id, AttentionChangeKind.Created, userB, occurredAtUtc: occurredAt);
        await dispatchB.WaitAsync(TimeSpan.FromSeconds(10));
        Volatile.Read(ref providerB).Should().Be(1,
            "item B's unrelated transport must complete while item A's async gate is paused — the gate read must not be under any shared lock");

        // Release item A's paused gate; A completes.
        gate.ReleasePause();
        await dispatchA.WaitAsync(TimeSpan.FromSeconds(10));
        Volatile.Read(ref providerA).Should().Be(1);
    }

    [Fact]
    public async Task DispatchAsync_TargetedResolvedSameLaneWhileCreatedPausedBeforeTransport_PublishesFenceAndReleasesOwnership()
    {
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var createdPrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int providerCalls = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            if (envelope.ChangeKind == AttentionChangeKind.Created)
            {
                createdPrepared.TrySetResult();
                await releaseCreated.Task.WaitAsync(cancellationToken);
            }

            NativePushTransportStartDecision decision =
                await transportStart.TryStartAsync(cancellationToken);
            if (!decision.IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCalls);
            return NativePushDispatchResult.Delivered();
        });
        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        DateTime createdAt = new(2026, 7, 15, 19, 0, 0, DateTimeKind.Utc);
        DateTime resolvedAt = createdAt.AddSeconds(1);

        Task created = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: createdAt);
        await createdPrepared.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Task resolved = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            userId,
            occurredAtUtc: resolvedAt);

        try
        {
            await resolved.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            releaseCreated.TrySetResult();
            await created.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Volatile.Read(ref providerCalls).Should().Be(0,
            "the same-lane resolution must publish its lifecycle fence before waiting on the lane, so the older prepared Created is vetoed at transport start");

        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            userId,
            occurredAtUtc: resolvedAt);

        attention.Verify(
            service => service.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()),
            Times.Exactly(3),
            "Created plus both exact-version Resolved attempts must reach the lookup, proving the first resolution released its lane and lifecycle participant ownership");
    }

    [Fact]
    public async Task DispatchAsync_GlobalResolvedSameLaneWhileCreatedPausedBeforeTransport_PublishesFenceAndReleasesOwnership()
    {
        // Global (untargeted) sibling of the targeted same-lane proof. A global
        // Resolved advances every fenced lifecycle through the distinct
        // PublishResolvedTombstoneAndFenceLifecycles + tracked-enumeration path,
        // whereas the targeted case uses PublishTargetedResolvedFence. Both must
        // publish their fence BEFORE waiting on the (item, null) lane so an older
        // untargeted Created paused in sender preparation is vetoed at its
        // transport-start boundary instead of the resolution being hidden behind
        // the lane until after transport starts.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var createdPrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int providerCalls = 0;
        var sender = new DelegateTransportSender(async (envelope, transportStart, cancellationToken) =>
        {
            if (envelope.ChangeKind == AttentionChangeKind.Created)
            {
                createdPrepared.TrySetResult();
                await releaseCreated.Task.WaitAsync(cancellationToken);
            }

            NativePushTransportStartDecision decision =
                await transportStart.TryStartAsync(cancellationToken);
            if (!decision.IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCalls);
            return NativePushDispatchResult.Delivered();
        });
        NativePushDispatcher sut = BuildWithScope(
            sender,
            gate.Object,
            tokens.Object,
            attention.Object,
            db,
            new NativePushSettings { Mode = NativePushMode.Direct, MaxAttempts = 1 });
        DateTime createdAt = new(2026, 7, 15, 19, 0, 0, DateTimeKind.Utc);
        DateTime resolvedAt = createdAt.AddSeconds(1);

        // Global Created (targetUserId: null) fans out to the single active
        // owner and pauses in sender preparation before its transport-start.
        Task created = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            targetUserId: null,
            occurredAtUtc: createdAt);
        await createdPrepared.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Global Resolved (targetUserId: null) on the SAME (item, null) lane.
        Task resolved = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt);

        try
        {
            // On 504dfb15 the lane is held across the paused Created, so this
            // resolution can never acquire it and this await deadlocks until the
            // timeout. The fix publishes the fence before the lane wait and only
            // holds the lane across the latest-version decision, so it completes.
            await resolved.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            releaseCreated.TrySetResult();
            await created.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Volatile.Read(ref providerCalls).Should().Be(0,
            "the same-lane global resolution must publish its tombstone + lifecycle fence before waiting on the lane, so the older prepared Created is vetoed at transport start");

        // The exact-version global Resolved retry must re-admit, proving the
        // first resolution released its lane participant and every fenced
        // lifecycle lease rather than leaving a live participant pinned.
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            targetUserId: null,
            occurredAtUtc: resolvedAt);

        Volatile.Read(ref providerCalls).Should().Be(0,
            "no stale Created generation may reach the provider even after the resolution and its retry complete");
        attention.Verify(
            service => service.FindItemAsync(userId, item.Id, It.IsAny<CancellationToken>()),
            Times.Exactly(3),
            "Created plus both exact-version global Resolved attempts must reach the lookup, proving the first resolution released its lane and lifecycle participant ownership");
    }

    [Fact]
    public async Task DispatchAsync_OptionsFailureSkipsRateExpiry_LongWindowBucketSurvivesAndRateLimitsAfterRecovery()
    {
        // Retained long-window failure/recovery guard. A single configured window
        // (ten minutes) is in effect throughout, so an options-read failure must not
        // drop the live bucket. Under the fixed design this holds because an options
        // failure skips settings-dependent rate-bucket expiry entirely.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        int providerCalls = 0;
        var sender = new DelegateTransportSender(async (_, transportStart, cancellationToken) =>
        {
            NativePushTransportStartDecision decision =
                await transportStart.TryStartAsync(cancellationToken);
            if (!decision.IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCalls);
            return NativePushDispatchResult.Delivered();
        });
        var settings = new NativePushSettings
        {
            Mode = NativePushMode.Direct,
            MaxAttempts = 1,
            RateLimitPerUser = 1,
            RateLimitWindow = TimeSpan.FromMinutes(10),
        };
        using var monitor = new ThrowingOptionsMonitor(settings);
        var timeProvider = new AdvancingTimeProvider(
            new DateTime(2026, 7, 15, 20, 0, 0, DateTimeKind.Utc));
        var services = new ServiceCollection();
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton(attention.Object);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        using var sut = new NativePushDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            sender,
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance,
            timeProvider);
        DateTime firstAt = timeProvider.GetUtcNow().UtcDateTime;

        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: firstAt);
        Volatile.Read(ref providerCalls).Should().Be(1);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        monitor.ArmThrowsForNextReads(1);
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            userId,
            occurredAtUtc: timeProvider.GetUtcNow().UtcDateTime);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: timeProvider.GetUtcNow().UtcDateTime);

        Volatile.Read(ref providerCalls).Should().Be(1,
            "an options-read failure must skip settings-dependent rate-bucket expiry so the live ten-minute bucket survives and the post-recovery send stays rate-limited");
    }

    [Fact]
    public async Task DispatchAsync_OptionsFailureWithStaleShortEntryWindow_DoesNotEvictLiveLongWindowBucket_RecoveryStaysRateLimited()
    {
        // Hicks r2 blocker 2 discriminating regression. Reproduces the unsafe
        // retained-window interleaving deterministically: a live rate bucket is
        // created under the authoritative long (ten-minute) window; a later dispatch
        // reads a stale shorter (thirty-second) window at its entry snapshot and then
        // fails its authoritative options read at the dispatch-core boundary. The
        // prior implementation pruned rate buckets on that options failure using the
        // stale shorter retained window, evicting the live bucket and permitting a
        // send after recovery. The fixed design skips settings-dependent rate expiry
        // on any options failure, so the live bucket survives and the recovery send
        // stays rate-limited. Fails on b5f5; passes fixed.
        Guid userId = Guid.NewGuid();
        AttentionItemDto item = BuildAttentionItem(AttentionKind.Offline);
        await using AppDbContext db = BuildDbContext();
        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);
        Mock<IDeviceTokenRepository> tokens = BuildDeviceTokens(userId);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        int providerCalls = 0;
        var sender = new DelegateTransportSender(async (_, transportStart, cancellationToken) =>
        {
            NativePushTransportStartDecision decision =
                await transportStart.TryStartAsync(cancellationToken);
            if (!decision.IsPermitted)
            {
                return NativePushDispatchResult.TransportStartVetoed();
            }

            Interlocked.Increment(ref providerCalls);
            return NativePushDispatchResult.Delivered();
        });

        static NativePushSettings WithWindow(TimeSpan window) => new()
        {
            Mode = NativePushMode.Direct,
            MaxAttempts = 1,
            RateLimitPerUser = 1,
            RateLimitWindow = window,
            DedupeWindow = TimeSpan.FromMinutes(1),
        };

        using var monitor = new ScriptedOptionsMonitor(WithWindow(TimeSpan.FromMinutes(10)));
        var timeProvider = new AdvancingTimeProvider(
            new DateTime(2026, 7, 15, 20, 0, 0, DateTimeKind.Utc));
        var services = new ServiceCollection();
        services.AddSingleton(gate.Object);
        services.AddSingleton(tokens.Object);
        services.AddSingleton(attention.Object);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        using var sut = new NativePushDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            sender,
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance,
            timeProvider);

        // 1) Establish the live bucket under the authoritative ten-minute window.
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: timeProvider.GetUtcNow().UtcDateTime);
        Volatile.Read(ref providerCalls).Should().Be(1, "the first send establishes the rate bucket");

        // 2) Ninety seconds later a dispatch reads a stale thirty-second window at its
        //    entry snapshot, then fails its authoritative options read at the core
        //    boundary. The options-failure finally prune runs (past the 30s internal
        //    prune cadence). Under b5f5 this evicts the live bucket with the stale 30s
        //    window; under the fix it skips rate-bucket expiry entirely.
        timeProvider.Advance(TimeSpan.FromSeconds(90));
        monitor.EnqueueValue(WithWindow(TimeSpan.FromSeconds(30))); // entry snapshot (Mode=Direct)
        monitor.EnqueueThrow();                                     // core read fails -> finally prune
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: timeProvider.GetUtcNow().UtcDateTime);
        Volatile.Read(ref providerCalls).Should().Be(1,
            "the options-failure dispatch throws before crossing transport");

        // 3) Recovery one second later. The bucket timestamp is ~91s old: within the
        //    ten-minute window (so a correct implementation stays rate-limited) but
        //    beyond the stale 30s window that the buggy retained-snapshot prune would
        //    have used to evict it.
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Created,
            userId,
            occurredAtUtc: timeProvider.GetUtcNow().UtcDateTime);

        Volatile.Read(ref providerCalls).Should().Be(1,
            "an options failure must not evict a live long-window bucket using a stale shorter window; the post-recovery send stays rate-limited");
    }

    // -----------------------------------------------------------------------
    // Fakes for the new blocker tests. Placed alongside the other test
    // helpers in this file so they compile without additional wiring.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Options monitor driven by a scripted queue of responses. Each read consumes the
    /// next queued action (a value or a throw); once the queue drains, reads return the
    /// fallback value. Used to reproduce the blocker-2 interleaving deterministically:
    /// enqueue a stale short-window value for the entry snapshot followed by a throw for
    /// the authoritative core read.
    /// </summary>
    private sealed class ScriptedOptionsMonitor : IOptionsMonitor<NativePushSettings>, IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<Func<NativePushSettings>> _script = new();
        private readonly NativePushSettings _fallback;

        public ScriptedOptionsMonitor(NativePushSettings fallback)
        {
            _fallback = fallback;
        }

        public NativePushSettings CurrentValue
        {
            get
            {
                lock (_sync)
                {
                    return _script.Count > 0 ? _script.Dequeue().Invoke() : _fallback;
                }
            }
        }

        public void EnqueueValue(NativePushSettings value)
        {
            lock (_sync)
            {
                _script.Enqueue(() => value);
            }
        }

        public void EnqueueThrow()
        {
            lock (_sync)
            {
                _script.Enqueue(static () => throw new InvalidOperationException("simulated options acquisition failure"));
            }
        }

        public NativePushSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<NativePushSettings, string?> listener) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Options monitor that can be armed to throw on the next N
    /// <see cref="CurrentValue"/> reads, simulating a validated-options
    /// reload throw. Subsequent reads return the configured normal value.
    /// </summary>
    private sealed class ThrowingOptionsMonitor : IOptionsMonitor<NativePushSettings>, IDisposable
    {
        private readonly NativePushSettings _normal;
        private int _remainingThrows;

        public ThrowingOptionsMonitor(NativePushSettings normal)
        {
            _normal = normal;
        }

        public NativePushSettings CurrentValue
        {
            get
            {
                if (Interlocked.Decrement(ref _remainingThrows) >= 0)
                {
                    throw new InvalidOperationException("simulated validated-options reload throw");
                }

                return _normal;
            }
        }

        public void ArmThrowsForNextReads(int count)
            => Interlocked.Exchange(ref _remainingThrows, count);

        public NativePushSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<NativePushSettings, string?> listener) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Production-realistic async operator-feature gate fake. Supports:
    /// <list type="bullet">
    ///   <item>Setting the effective enabled flag.</item>
    ///   <item>Pausing async reads on a specific call index (e.g., the
    ///     transport-start read) so tests can flip the enabled flag or
    ///     cancel the dispatch while the gate read is in flight.</item>
    ///   <item>Throwing on a specific call index to simulate a DB outage.</item>
    /// </list>
    /// The synchronous <see cref="IsEnabled"/> path returns the flag
    /// unconditionally — it exists only so the type satisfies the
    /// interface for consumers that still take the sync surface.
    /// </summary>
    private sealed class ControllableAsyncGate : IOperatorFeatureGate, IDisposable
    {
        private readonly object _sync = new();
        private int _asyncCalls;
        private volatile bool _enabled = true;
        private int _pauseAfterCall = -1;
        private TaskCompletionSource? _pauseTcs;
        private TaskCompletionSource? _pauseObservedTcs;
        private TaskCompletionSource? _activePauseTcs;
        private int _throwOnCall = -1;
        private Exception? _throwWith;

        public void SetEnabled(bool enabled) => _enabled = enabled;

        public void ArmPauseForCallCounts(int pauseAfterCall)
        {
            lock (_sync)
            {
                _pauseAfterCall = pauseAfterCall;
                _pauseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pauseObservedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void DisarmPause()
        {
            lock (_sync)
            {
                // Only disarm the arming state; the already-active TCS is preserved
                // so <see cref="ReleasePause"/> can still complete an in-flight
                // paused await after a disarm. Tests deliberately disarm to let
                // unrelated concurrent gate reads pass through without pausing.
                _pauseAfterCall = -1;
                _pauseTcs = null;
                _pauseObservedTcs = null;
            }
        }

        public void ReleasePause()
        {
            TaskCompletionSource? tcs;
            lock (_sync)
            {
                tcs = _activePauseTcs ?? _pauseTcs;
            }

            tcs?.TrySetResult();
        }

        public Task WaitForPausedAsync()
        {
            TaskCompletionSource? tcs;
            lock (_sync)
            {
                tcs = _pauseObservedTcs;
            }

            return tcs?.Task ?? Task.CompletedTask;
        }

        public void ArmThrowOnCall(int callIndex, Exception ex)
        {
            lock (_sync)
            {
                _throwOnCall = callIndex;
                _throwWith = ex;
            }
        }

        public void DisarmThrow()
        {
            lock (_sync)
            {
                _throwOnCall = -1;
                _throwWith = null;
            }
        }

        public bool IsEnabled(OperatorFeature feature) => _enabled;

        public Task<bool> IsEnabledAsync(OperatorFeature feature, CancellationToken cancellationToken = default)
            => EvaluateAsync(cancellationToken);

        public Task<bool> IsEnabledStrictAsync(OperatorFeature feature, CancellationToken cancellationToken = default)
            => EvaluateAsync(cancellationToken);

        // Both async surfaces share one machinery so the dispatcher's strict
        // transport-start reads drive the same call counter, pause, and throw
        // arming the tests rely on (Hicks r2 blocker 1). The dispatcher calls the
        // strict path exclusively, so ArmThrowOnCall/ArmPauseForCallCounts indices
        // are unchanged from when it called the general path.
        private async Task<bool> EvaluateAsync(CancellationToken cancellationToken)
        {
            int callIndex = Interlocked.Increment(ref _asyncCalls);

            TaskCompletionSource? pauseTcs = null;
            TaskCompletionSource? pauseObservedTcs = null;
            int throwOnCall;
            Exception? throwWith;
            int pauseAfterCall;
            lock (_sync)
            {
                pauseAfterCall = _pauseAfterCall;
                throwOnCall = _throwOnCall;
                throwWith = _throwWith;
                if (pauseAfterCall >= 0 && callIndex == pauseAfterCall + 1)
                {
                    pauseTcs = _pauseTcs;
                    pauseObservedTcs = _pauseObservedTcs;
                    _activePauseTcs = pauseTcs;
                }
            }

            if (throwOnCall == callIndex && throwWith is not null)
            {
                throw throwWith;
            }

            if (pauseTcs is not null)
            {
                pauseObservedTcs?.TrySetResult();
                await pauseTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return _enabled;
        }

        public bool IsHardDisabledByEnvironment(OperatorFeature feature) => false;

        public string GetFlagName(OperatorFeature feature) => feature.ToString();

        public IReadOnlyList<(OperatorFeature Feature, string FlagName)> AllFeatures =>
            new[] { (OperatorFeature.NativePush, "nativePushEnabled") };

        public OperatorFeatureFlagsDto GetEffectiveFlags() => new()
        {
            NativePushEnabled = _enabled,
        };

        public void Dispose()
        {
            TaskCompletionSource? tcs;
            lock (_sync)
            {
                tcs = _pauseTcs;
                _pauseTcs = null;
            }

            tcs?.TrySetResult();
        }
    }

    private static Farm.Infrastructure.Domain.User BuildUser(Guid userId, string name)
    {
        return new Farm.Infrastructure.Domain.User
        {
            Id = userId,
            Username = $"{name}-{userId:N}",
            Email = $"{name}-{userId:N}@test.local",
            PasswordHash = "x",
        };
    }

    private static NotificationPreferences BuildPushPreferences(Guid userId)
    {
        return new NotificationPreferences
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            EnablePushNotifications = true,
            PushOnPrinterOffline = true,
            AttentionPushCategoryPreferencesJson = null,
        };
    }

    private static DeviceToken MakeToken(Guid userId, string installationId)
    {
        return new DeviceToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            InstallationId = installationId,
            Token = installationId + "-token".PadRight(64, 'A'),
            Platform = "ios",
            Environment = "development",
            IsActive = true,
        };
    }

    private static NativePushDispatcher Build(INativePushSender sender, IServiceScopeFactory scopes, NativePushMode mode)
    {
        IOptionsMonitor<NativePushSettings> monitor = new StaticOptionsMonitor(new NativePushSettings { Mode = mode });
        return new NativePushDispatcher(
            scopes,
            AsTransportAwareForTests(sender),
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);
    }

    private static NativePushDispatcher BuildWithScope(
        Mock<INativePushSender> sender,
        IOperatorFeatureGate gate,
        IDeviceTokenRepository tokens,
        IAttentionService attention,
        AppDbContext db,
        NativePushSettings? settings = null,
        TimeProvider? timeProvider = null,
        NativePushMetrics? metrics = null,
        IServerIdentityService? serverIdentity = null)
    {
        return BuildWithScope(
            sender.Object,
            gate,
            tokens,
            attention,
            db,
            settings,
            timeProvider,
            metrics,
            serverIdentity);
    }

    private static NativePushDispatcher BuildWithScope(
        INativePushSender sender,
        IOperatorFeatureGate gate,
        IDeviceTokenRepository tokens,
        IAttentionService attention,
        AppDbContext db,
        NativePushSettings? settings = null,
        TimeProvider? timeProvider = null,
        NativePushMetrics? metrics = null,
        IServerIdentityService? serverIdentity = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gate);
        services.AddSingleton(tokens);
        services.AddSingleton(attention);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        IOptionsMonitor<NativePushSettings> monitor = new StaticOptionsMonitor(
            settings ?? new NativePushSettings { Mode = NativePushMode.Relay });
        return new NativePushDispatcher(
            scopeFactory,
            AsTransportAwareForTests(sender),
            monitor,
            metrics ?? new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance,
            timeProvider,
            serverIdentity);
    }

    private static AppDbContext BuildDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static Mock<IOperatorFeatureGate> BuildGate(bool enabled)
    {
        var gate = new Mock<IOperatorFeatureGate>();
        // Keep the sync IsEnabled set for controllers/services that still take
        // the synchronous surface, and mirror the same value on BOTH async paths:
        // the general fallback <see cref="IOperatorFeatureGate.IsEnabledAsync"/> and
        // the strict <see cref="IOperatorFeatureGate.IsEnabledStrictAsync"/> path the
        // dispatcher's transport-start handshake now uses (Hicks r2 blocker 1). All
        // three must agree so tests that build a single gate keep behaving as before.
        gate.Setup(g => g.IsEnabled(OperatorFeature.NativePush)).Returns(enabled);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.NativePush, It.IsAny<CancellationToken>()))
            .ReturnsAsync(enabled);
        gate.Setup(g => g.IsEnabledStrictAsync(OperatorFeature.NativePush, It.IsAny<CancellationToken>()))
            .ReturnsAsync(enabled);
        return gate;
    }

    private static Mock<IDeviceTokenRepository> BuildDeviceTokens(Guid userId, int deviceCount = 1)
    {
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens
            .Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { userId });
        tokens
            .Setup(r => r.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, deviceCount).Select(i => new DeviceToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                InstallationId = $"test-install-{i}",
                Token = $"{i:D2}".PadRight(64, 'A'),
                Platform = "ios",
                Environment = "development",
                IsActive = true,
            }).ToList());
        return tokens;
    }

    private static Mock<IAttentionService> BuildAttention(Guid userId, string itemId, AttentionItemDto item)
    {
        var attention = new Mock<IAttentionService>();
        attention
            .Setup(s => s.FindItemAsync(userId, itemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        return attention;
    }

    private static AttentionItemDto BuildAttentionItem(AttentionKind kind)
    {
        return new AttentionItemDto(
            Id: $"{kind.ToString().ToLowerInvariant()}:{Guid.NewGuid():N}",
            Kind: kind,
            Severity: AttentionSeverity.Warning,
            PrinterId: Guid.NewGuid(),
            PrinterName: "Printer-1",
            Title: "Test",
            Detail: "Test detail",
            OccurredAt: DateTime.UtcNow,
            Actions: Array.Empty<AttentionActionDto>());
    }

    private sealed class TokenOutcomeProbe
    {
        private int _successWrites;
        private int _failureWrites;
        private int _invalidationWrites;

        public int SuccessWrites => Volatile.Read(ref _successWrites);

        public int FailureWrites => Volatile.Read(ref _failureWrites);

        public int InvalidationWrites => Volatile.Read(ref _invalidationWrites);

        public void RecordSuccess() => Interlocked.Increment(ref _successWrites);

        public void RecordFailure() => Interlocked.Increment(ref _failureWrites);

        public void RecordInvalidation() => Interlocked.Increment(ref _invalidationWrites);
    }

    private sealed class OutcomeRecordingDeviceTokenRepository(
        IDeviceTokenRepository inner,
        TokenOutcomeProbe probe) : IDeviceTokenRepository
    {
        public Task<DeviceToken> UpsertAsync(
            Guid userId,
            string installationId,
            string token,
            string platform,
            string environment,
            string? appBundleId,
            CancellationToken cancellationToken = default)
            => inner.UpsertAsync(
                userId,
                installationId,
                token,
                platform,
                environment,
                appBundleId,
                cancellationToken);

        public Task<bool> DeleteByInstallationAsync(
            Guid userId,
            string installationId,
            CancellationToken cancellationToken = default)
            => inner.DeleteByInstallationAsync(userId, installationId, cancellationToken);

        public Task<IReadOnlyList<DeviceToken>> GetActiveByUserAsync(
            Guid userId,
            CancellationToken cancellationToken = default)
            => inner.GetActiveByUserAsync(userId, cancellationToken);

        public Task<IReadOnlyList<Guid>> GetActiveTokenOwnersAsync(
            CancellationToken cancellationToken = default)
            => inner.GetActiveTokenOwnersAsync(cancellationToken);

        public Task RecordSuccessAsync(
            Guid deviceTokenId,
            long registrationVersion,
            DateTime nowUtc,
            CancellationToken cancellationToken = default)
        {
            probe.RecordSuccess();
            return inner.RecordSuccessAsync(
                deviceTokenId,
                registrationVersion,
                nowUtc,
                cancellationToken);
        }

        public Task RecordFailureAsync(
            Guid deviceTokenId,
            long registrationVersion,
            DateTime nowUtc,
            int failureThreshold,
            CancellationToken cancellationToken = default)
        {
            probe.RecordFailure();
            return inner.RecordFailureAsync(
                deviceTokenId,
                registrationVersion,
                nowUtc,
                failureThreshold,
                cancellationToken);
        }

        public Task<bool> InvalidateAsync(
            Guid deviceTokenId,
            long registrationVersion,
            CancellationToken cancellationToken = default)
        {
            probe.RecordInvalidation();
            return inner.InvalidateAsync(deviceTokenId, registrationVersion, cancellationToken);
        }
    }

    private sealed class TokenOutcomeDeleteRaceInterceptor(Guid doomedTokenId) : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<DbContextId> _persistenceContextIds = new();
        private int _doomedUpdateObserved;

        public TaskCompletionSource TokenAUpdateReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DeleteCommitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<DbContextId> PersistenceContextIds =>
            _persistenceContextIds.ToArray();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("UPDATE \"DeviceTokens\"", StringComparison.Ordinal))
            {
                return result;
            }

            _persistenceContextIds.Enqueue(eventData.Context!.ContextId);
            if (Interlocked.CompareExchange(ref _doomedUpdateObserved, 1, 0) == 0)
            {
                command.Parameters.Cast<DbParameter>()
                    .Should().Contain(parameter => Equals(parameter.Value, doomedTokenId));
                TokenAUpdateReady.TrySetResult();
                await DeleteCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }

    private sealed class RecordingDispatcherLogger : ILogger<NativePushDispatcher>
    {
        private readonly ConcurrentQueue<Exception> _exceptions = new();
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyCollection<Exception> Exceptions => _exceptions.ToArray();

        public IReadOnlyCollection<string> Warnings => _warnings.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                _exceptions.Enqueue(exception);
            }

            if (logLevel == LogLevel.Warning)
            {
                _warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed class DelegateTransportSender(
        Func<NativePushEnvelope, INativePushTransportStart, CancellationToken, Task<NativePushDispatchResult>> send)
        : INativePushTransportSender
    {
        public string ModeName => "test";

        public Task<NativePushDispatchResult> SendAsync(
            NativePushEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Dispatcher tests must use the typed transport-start overload.");
        }

        public Task<NativePushDispatchResult> SendAsync(
            NativePushEnvelope envelope,
            INativePushTransportStart transportStart,
            CancellationToken cancellationToken = default)
        {
            return send(envelope, transportStart, cancellationToken);
        }
    }

    private static INativePushTransportSender AsTransportAwareForTests(INativePushSender sender)
    {
        return sender as INativePushTransportSender ?? new AtomicTestTransportSender(sender);
    }

    private sealed class AtomicTestTransportSender(INativePushSender inner) : INativePushTransportSender
    {
        public string ModeName => inner.ModeName;

        public Task<NativePushDispatchResult> SendAsync(
            NativePushEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            return inner.SendAsync(envelope, cancellationToken);
        }

        public async Task<NativePushDispatchResult> SendAsync(
            NativePushEnvelope envelope,
            INativePushTransportStart transportStart,
            CancellationToken cancellationToken = default)
        {
            NativePushTransportStartDecision decision = await transportStart
                .TryStartAsync(cancellationToken)
                .ConfigureAwait(false);
            return decision.IsPermitted
                ? await inner.SendAsync(envelope, cancellationToken).ConfigureAwait(false)
                : NativePushDispatchResult.TransportStartVetoed();
        }
    }

    public enum GlobalResolutionEarlyExit
    {
        DispatcherDisabled,
        FeatureDisabled,
        ScopeCreationFailure,
        ActiveOwnerLookupFailure,
    }

    private sealed class ToggleFailingServiceScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public bool FailNext { get; set; }

        public IServiceScope CreateScope()
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("simulated scope creation failure");
            }

            return inner.CreateScope();
        }
    }

    /// <summary>
    /// Wraps a real <see cref="IServiceScopeFactory"/> and deterministically
    /// blocks the FIRST call to <see cref="CreateScope"/> until released,
    /// letting every subsequent call through immediately. CreateAsyncScope
    /// (used by NativePushDispatcher) synchronously delegates to
    /// CreateScope — it is the one point before TryObserveLifecycle where a
    /// targeted DispatchAsync call can be paused deterministically. The
    /// blocked call is intended to run on a background thread (started via
    /// Task.Run) so the test method's own thread is never blocked.
    /// </summary>
    private sealed class FirstCallGatedServiceScopeFactory(
        IServiceScopeFactory inner,
        TaskCompletionSource entered,
        TaskCompletionSource release) : IServiceScopeFactory
    {
        private int _callCount;

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }

            return inner.CreateScope();
        }
    }

    private sealed class ControlledRetryTimeProvider(DateTime nowUtc) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly DateTimeOffset _now = new(nowUtc, TimeSpan.Zero);
        private readonly TaskCompletionSource _retryDelayStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ControlledTimer? _retryTimer;

        public Task RetryDelayStarted => _retryDelayStarted.Task;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ControlledTimer(callback, state);
            lock (_sync)
            {
                _retryTimer = timer;
            }

            _retryDelayStarted.TrySetResult();
            return timer;
        }

        public void ReleaseRetry()
        {
            ControlledTimer? timer;
            lock (_sync)
            {
                timer = _retryTimer;
            }

            timer?.Fire();
        }

        private sealed class ControlledTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _completed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _completed) == 0;

            public void Dispose() => Interlocked.Exchange(ref _completed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                if (Interlocked.Exchange(ref _completed, 1) == 0)
                {
                    callback(state);
                }
            }
        }
    }

    private sealed class ImmediateTimeProvider(DateTime nowUtc) : TimeProvider
    {
        private readonly DateTimeOffset _now = new(nowUtc, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            TimeSpan immediateDueTime = dueTime == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.Zero;
            return new Timer(callback, state, immediateDueTime, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Test-only <see cref="TimeProvider"/> whose current time is
    /// manually advanced. Used by the Hicks r2 blocker 1 leak-proof
    /// tests to drive PruneCaches past its 30-second internal rate
    /// limit and past the seven-day AttentionSnapshotTtl retention
    /// TTL without wall-clock sleeps.
    /// </summary>
    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private long _ticks;

        public AdvancingTimeProvider(DateTime initialUtc)
        {
            _ticks = initialUtc.Ticks;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return new(new DateTime(Interlocked.Read(ref _ticks), DateTimeKind.Utc), TimeSpan.Zero);
        }

        public void Advance(TimeSpan delta)
        {
            _ = Interlocked.Add(ref _ticks, delta.Ticks);
        }
    }

    private sealed class StaticOptionsMonitor(NativePushSettings value) : IOptionsMonitor<NativePushSettings>
    {
        public NativePushSettings CurrentValue { get; } = value;

        public NativePushSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<NativePushSettings, string?> listener) => null;
    }

    private sealed class MutableOptionsMonitor(NativePushSettings value) : IOptionsMonitor<NativePushSettings>
    {
        public NativePushSettings CurrentValue { get; set; } = value;

        public NativePushSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<NativePushSettings, string?> listener) => null;
    }
}
