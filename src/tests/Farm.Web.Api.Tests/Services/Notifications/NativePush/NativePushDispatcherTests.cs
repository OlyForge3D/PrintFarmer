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
