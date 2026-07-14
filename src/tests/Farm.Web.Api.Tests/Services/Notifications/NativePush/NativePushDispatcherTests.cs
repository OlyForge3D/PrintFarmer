using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain.Notifications;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Notifications;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Notifications.NativePush;
using Farm.Infrastructure.Services.OperatorFeatures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task DispatchAsync_PersistFailureForOneToken_ContinuesRemainingTokensAndOwners()
    {
        // Vasquez v6 B1 regression: when persisting the send result for the
        // first token throws (RecordSuccessAsync), the remaining tokens for
        // the same owner AND the other owner's tokens must still be
        // dispatched. Before the fix a single-line try/catch wrapped the
        // entire fan-out and any per-token persistence failure aborted every
        // remaining device. This regression exercises two owners with two
        // tokens each, and forces the first-token persist to throw.
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

        // Attention service resolves the item for BOTH owners (attention is
        // per-user in the current model but the same synthesized item is fine
        // for our purposes — the dispatcher will call FindItemAsync once per
        // owner).
        var attention = new Mock<IAttentionService>();
        attention
            .Setup(s => s.FindItemAsync(ownerA, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        attention
            .Setup(s => s.FindItemAsync(ownerB, item.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        // Two tokens for owner A, two for owner B.
        DeviceToken ownerAToken1 = MakeToken(ownerA, "install-a1");
        DeviceToken ownerAToken2 = MakeToken(ownerA, "install-a2");
        DeviceToken ownerBToken1 = MakeToken(ownerB, "install-b1");
        DeviceToken ownerBToken2 = MakeToken(ownerB, "install-b2");

        var tokens = new Mock<IDeviceTokenRepository>();
        tokens
            .Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { ownerA, ownerB });
        tokens
            .Setup(r => r.GetActiveByUserAsync(ownerA, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken> { ownerAToken1, ownerAToken2 });
        tokens
            .Setup(r => r.GetActiveByUserAsync(ownerB, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DeviceToken> { ownerBToken1, ownerBToken2 });

        // The first token's persistence throws. Every subsequent persistence
        // succeeds. This isolates the failure precisely to the ApplyResultAsync
        // step for exactly one token — mirroring an operator database blip.
        int recordSuccessCallCount = 0;
        tokens
            .Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, DateTime, CancellationToken>((id, ts, ct) =>
            {
                int callIndex = System.Threading.Interlocked.Increment(ref recordSuccessCallCount);
                if (callIndex == 1)
                {
                    return Task.FromException(new InvalidOperationException("simulated persist failure for first token"));
                }

                return Task.CompletedTask;
            });

        Mock<IOperatorFeatureGate> gate = BuildGate(enabled: true);

        int sendCallCount = 0;
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((env, ct) =>
            {
                System.Threading.Interlocked.Increment(ref sendCallCount);
                return Task.FromResult(NativePushDispatchResult.Delivered());
            });

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        // Every token must have received a send attempt — 4 total.
        sendCallCount.Should().Be(4, "the first token's persist failure must not abort remaining tokens or owners");
        recordSuccessCallCount.Should().Be(4, "every device should still be persisted after the isolated failure");
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
            .Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
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
    public async Task DispatchAsync_SenderThrowsOceWithInternalToken_PropagatesWhenCallerTokenIsNone()
    {
        // Hicks #1 regression: an OperationCanceledException raised from an
        // INTERNAL or linked token (a timeout inside the sender, an inner
        // linked cts, etc.) must propagate out of DispatchAsync even when
        // the caller passed CancellationToken.None. The prior code guarded
        // every isolation catch on `cancellationToken.IsCancellationRequested`
        // which was false when only the internal token tripped — so the OCE
        // fell through into the generic Exception catch and was swallowed,
        // masking cancellation as a delivery blip.
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
            .Setup(r => r.RecordFailureAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        // Sender throws OCE with an internal token that has ALREADY been
        // cancelled — mimicking a per-attempt timeout inside the sender.
        using var innerCts = new CancellationTokenSource();
        innerCts.Cancel();

        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((_, _) =>
                Task.FromException<NativePushDispatchResult>(new OperationCanceledException(innerCts.Token)));

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        // Caller cancellation token is None — the whole point of Hicks #1 is
        // that this MUST still propagate the inner OCE out of DispatchAsync.
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
            .Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, DateTime, CancellationToken>((_, _, _) => Task.FromException(new OperationCanceledException(innerCts.Token)));
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
            .Setup(r => r.RecordFailureAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
            .Setup(r => r.RecordFailureAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
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

    [Fact]
    public async Task DispatchAsync_UnknownTerminalReason_StillDeactivatesToken()
    {
        // Contra-positive to the H5 allow-list: novel terminal reasons must
        // still tick the failure counter so genuinely broken tokens retire.
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
            .Setup(r => r.RecordFailureAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => System.Threading.Interlocked.Increment(ref recordFailureCount))
            .Returns(Task.CompletedTask);
        Mock<IAttentionService> attention = BuildAttention(userId, item.Id, item);

        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender
            .Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(NativePushDispatchResult.Terminal("BadDeviceToken"));

        NativePushDispatcher sut = BuildWithScope(sender, gate.Object, tokens.Object, attention.Object, db);

        await sut.DispatchAsync(item.Id, AttentionChangeKind.Created, targetUserId: null);

        recordFailureCount.Should().Be(1, "unrecognised terminal reasons still count against the token");
    }

    [Fact]
    public async Task DispatchAsync_RateLimit_IsChargedOncePerEnvelopeAcrossDevices()
    {
        // Hicks H2-v5-final regression: rate limit is (userId, printerId,
        // kind)-scoped and charged BEFORE per-device fan-out. A three-device
        // user must not exhaust the bucket three times faster than a
        // one-device user.
        var userId = Guid.NewGuid();
        AttentionItemDto item1 = BuildAttentionItem(AttentionKind.Offline);
        AttentionItemDto item2 = BuildAttentionItem(AttentionKind.Offline) with { PrinterId = item1.PrinterId };

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
            .Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var attention = new Mock<IAttentionService>();
        attention.Setup(s => s.FindItemAsync(userId, item1.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item1);
        attention.Setup(s => s.FindItemAsync(userId, item2.Id, It.IsAny<CancellationToken>())).ReturnsAsync(item2);

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
            sender.Object,
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);

        await sut.DispatchAsync(item1.Id, AttentionChangeKind.Created, targetUserId: null);
        int sendCountAfterFirstEnvelope = sendCount;

        await sut.DispatchAsync(item2.Id, AttentionChangeKind.Created, targetUserId: null);

        sendCountAfterFirstEnvelope.Should().Be(3, "rate bucket is consumed once per envelope; all three devices must be reached");
        sendCount.Should().Be(3, "second envelope for the same (user, printer, kind) is rate-limited");
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
            .Setup(r => r.RecordSuccessAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
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
            sender.Object,
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
            sender,
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance);
    }

    private static NativePushDispatcher BuildWithScope(
        Mock<INativePushSender> sender,
        IOperatorFeatureGate gate,
        IDeviceTokenRepository tokens,
        IAttentionService attention,
        AppDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gate);
        services.AddSingleton(tokens);
        services.AddSingleton(attention);
        services.AddSingleton(db);
        ServiceProvider provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return Build(sender.Object, scopeFactory, NativePushMode.Relay);
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
        gate.Setup(g => g.IsEnabled(OperatorFeature.NativePush)).Returns(enabled);
        return gate;
    }

    private static Mock<IDeviceTokenRepository> BuildDeviceTokens(Guid userId)
    {
        var tokens = new Mock<IDeviceTokenRepository>();
        tokens
            .Setup(r => r.GetActiveTokenOwnersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { userId });
        tokens
            .Setup(r => r.GetActiveByUserAsync(userId, It.IsAny<CancellationToken>()))
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

    private sealed class StaticOptionsMonitor(NativePushSettings value) : IOptionsMonitor<NativePushSettings>
    {
        public NativePushSettings CurrentValue { get; } = value;

        public NativePushSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<NativePushSettings, string?> listener) => null;
    }
}
