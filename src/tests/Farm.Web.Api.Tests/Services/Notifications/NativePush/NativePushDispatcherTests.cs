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
                sender.Object,
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
                sender.Object,
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
                sender.Object,
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
            sender.Object,
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
            sender.Object,
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
            sender.Object,
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
                sender.Object,
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
    public async Task DispatchAsync_CreatedBlockedBeforeSnapshot_ResolvedWaitsForAlert()
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
        await createdLookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task resolved = sut.DispatchAsync(
            item.Id,
            AttentionChangeKind.Resolved,
            userId,
            occurredAtUtc: createdAt.AddSeconds(1));

        sent.Should().BeEmpty();
        resolved.IsCompleted.Should().BeFalse();

        releaseCreatedLookup.TrySetResult();
        await Task.WhenAll(created, resolved).WaitAsync(TimeSpan.FromSeconds(10));

        sent.Should().Equal(AttentionChangeKind.Created, AttentionChangeKind.Resolved);
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

            sent.Should().Equal(AttentionChangeKind.Updated, AttentionChangeKind.Resolved);

            clock.ReleaseRetry();
            await updated.WaitAsync(TimeSpan.FromSeconds(10));

            sent.Should().Equal(
                new[] { AttentionChangeKind.Updated, AttentionChangeKind.Resolved },
                "the consumed snapshot makes the pending targeted retry obsolete");
            sender.Verify(value => value.SendAsync(
                    It.IsAny<NativePushEnvelope>(),
                    It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            tokens.Verify(repository => repository.RecordSuccessAsync(
                    It.IsAny<Guid>(),
                    It.IsAny<long>(),
                    It.IsAny<DateTime>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
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
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender.Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((_, _) =>
            {
                if (Interlocked.Increment(ref attemptCount) == 1)
                {
                    throw new InvalidOperationException("simulated synchronous sender failure before transport");
                }

                return Task.FromResult(NativePushDispatchResult.Delivered());
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
        var sender = new Mock<INativePushSender>();
        sender.SetupGet(s => s.ModeName).Returns("direct");
        sender.Setup(s => s.SendAsync(It.IsAny<NativePushEnvelope>(), It.IsAny<CancellationToken>()))
            .Returns<NativePushEnvelope, CancellationToken>((envelope, _) =>
            {
                Guid tokenId = Guid.Parse(envelope.DeviceTokenId);
                attemptedDevices.Enqueue(tokenId);
                int attempt = perDeviceCounts.AddOrUpdate(tokenId, 1, (_, prev) => prev + 1);
                if (attempt == 1)
                {
                    // Synchronous throw BEFORE any awaitable transport work
                    // has started. Simulates typed-transport guard rejection,
                    // HttpClient factory construction failure, or any
                    // pre-await invariant violation inside the sender.
                    throw new InvalidOperationException(
                        $"kane #755 cycle 3: simulated synchronous sender failure before transport (device={tokenId})");
                }

                return Task.FromResult(NativePushDispatchResult.Delivered());
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
                sender.Object,
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
        AppDbContext db,
        NativePushSettings? settings = null,
        TimeProvider? timeProvider = null)
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
            sender.Object,
            monitor,
            new NativePushMetrics(),
            NullLogger<NativePushDispatcher>.Instance,
            timeProvider);
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

        public IReadOnlyCollection<Exception> Exceptions => _exceptions.ToArray();

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

    private sealed class StaticOptionsMonitor(NativePushSettings value) : IOptionsMonitor<NativePushSettings>
    {
        public NativePushSettings CurrentValue { get; } = value;

        public NativePushSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<NativePushSettings, string?> listener) => null;
    }
}
