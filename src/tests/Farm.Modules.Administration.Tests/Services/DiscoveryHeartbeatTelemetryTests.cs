using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Repositories.Settings;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Settings;
using Farm.Modules.Administration.Services.Workers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.Administration.Tests.Services;

/// <summary>
/// Regression coverage for issue #2995: corrupt discovery heartbeat telemetry must not break
/// settings loading or checked saves, and must be reported as unknown liveness. Also covers
/// issue #3019: a checked save never persists a client-supplied heartbeat, and issue #3023: the
/// unchecked <see cref="SettingsService.Save{T}"/> used by bulk <c>POST /api/settings</c> doesn't either.
/// </summary>
public sealed class DiscoveryHeartbeatTelemetryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly List<AppDbContext> _repositoryContexts = [];

    public DiscoveryHeartbeatTelemetryTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using AppDbContext db = new(_options);
        db.Database.EnsureCreated();
    }

    public static TheoryData<string> CorruptHeartbeatPayloads() => new()
    {
        "{not json",
        string.Empty,
        "null",
        "42",
        "{\"at\":\"2026-01-01T00:00:00Z\"}",
        "\"not-a-timestamp\"",
        "\"2026-13-45T99:00:00Z\"",
        JsonSerializer.Serialize(DateTime.UtcNow.AddDays(1)),
    };

    [Theory]
    [MemberData(nameof(CorruptHeartbeatPayloads))]
    public void Load_CorruptHeartbeat_KeepsUnrelatedSettingsAndReportsUnknownLiveness(string payload)
    {
        SeedSection("UpdateChannel", new UpdateChannelSettings { Channel = "insider", InsiderAcknowledged = true });
        SeedSection(NetworkDiscoverySettings.SectionName, new NetworkDiscoverySettings { ClientTimeoutMs = 700, LastHeartbeat = DateTime.UtcNow });
        SeedTelemetry(payload);
        Mock<ILogger<SettingsService>> logger = new();

        SettingsService service = CreateService(logger.Object);

        service.Get<UpdateChannelSettings>().Channel.Should().Be("insider");
        NetworkDiscoverySettings discovery = service.Get<NetworkDiscoverySettings>();
        discovery.ClientTimeoutMs.Should().Be(700);
        discovery.LastHeartbeat.Should().BeNull("corrupt telemetry must not fall back to the legacy timestamp");
        discovery.HeartbeatTelemetryUnreadable.Should().BeTrue();
        VerifyTelemetryWarning(logger);
    }

    [Fact]
    public void Load_ValidHeartbeat_AppliesUtcTimestamp()
    {
        DateTime heartbeat = DateTime.UtcNow.AddSeconds(-10);
        SeedTelemetry(JsonSerializer.Serialize(new DateTimeOffset(heartbeat).ToOffset(TimeSpan.FromHours(5))));
        Mock<ILogger<SettingsService>> logger = new();

        NetworkDiscoverySettings discovery = CreateService(logger.Object).Get<NetworkDiscoverySettings>();

        discovery.HeartbeatTelemetryUnreadable.Should().BeFalse();
        discovery.LastHeartbeat.Should().NotBeNull();
        discovery.LastHeartbeat!.Value.Kind.Should().Be(DateTimeKind.Utc);
        discovery.LastHeartbeat.Value.Should().BeCloseTo(heartbeat, TimeSpan.FromMilliseconds(1));
        logger.Invocations.Should().NotContain(i => i.Arguments.Count > 0 && Equals(i.Arguments[0], LogLevel.Warning));
    }

    [Fact]
    public void Load_HeartbeatWithinClockSkewTolerance_IsAccepted()
    {
        DateTime heartbeat = DateTime.UtcNow.AddMinutes(1);
        SeedTelemetry(JsonSerializer.Serialize(heartbeat));

        NetworkDiscoverySettings discovery = CreateService().Get<NetworkDiscoverySettings>();

        discovery.HeartbeatTelemetryUnreadable.Should().BeFalse();
        discovery.LastHeartbeat.Should().Be(heartbeat);
    }

    [Theory]
    [InlineData("\"not-a-timestamp\"")]
    [InlineData("{not json")]
    public async Task CheckedSave_CorruptHeartbeat_SavesSectionAndLeavesTelemetryAndCasIntactAsync(string payload)
    {
        SettingsService seeder = CreateService();
        SettingsSectionSnapshot seeded = await seeder.SaveWithConcurrencyCheckAsync(
            new NetworkDiscoverySettings(), SettingsSectionSnapshot.AbsentRowVersion);
        SeedTelemetry(payload);
        Mock<ILogger<SettingsService>> logger = new();
        SettingsService service = CreateService(logger.Object);
        VerifyTelemetryWarning(logger);
        logger.Invocations.Clear();
        SettingsService stale = CreateService();
        SettingsSectionSnapshot staleSnapshot = stale.GetSectionSnapshot(NetworkDiscoverySettings.SectionName);
        staleSnapshot.RowVersion.Should().Be(seeded.RowVersion);

        SettingsSectionSnapshot saved = await service.SaveWithConcurrencyCheckAsync(
            new NetworkDiscoverySettings { ClientTimeoutMs = 900, LastHeartbeat = DateTime.UtcNow }, seeded.RowVersion);

        saved.RowVersion.Should().NotBe(seeded.RowVersion);
        NetworkDiscoverySettings savedValue = (NetworkDiscoverySettings)saved.Value;
        savedValue.ClientTimeoutMs.Should().Be(900);
        savedValue.LastHeartbeat.Should().BeNull("a client-supplied or legacy timestamp must not mask corrupt telemetry");
        savedValue.HeartbeatTelemetryUnreadable.Should().BeTrue();
        VerifyTelemetryWarning(logger);

        using (AppDbContext db = new(_options))
        {
            AppSettingsEntity section = await db.AppSettingsEntities.SingleAsync(e => e.Key == NetworkDiscoverySettings.SectionName);
            section.SettingsJson.Should().NotContain("lastHeartbeat");
            section.SettingsJson.Should().NotContain(nameof(NetworkDiscoverySettings.HeartbeatTelemetryUnreadable));
            AppSettingsEntity telemetry = await db.AppSettingsEntities.SingleAsync(e => e.Key == NetworkDiscoverySettings.HeartbeatStorageKey);
            telemetry.SettingsJson.Should().Be(payload);
            telemetry.Revision.Should().Be(1);
        }

        Func<Task> staleSave = () => stale.SaveWithConcurrencyCheckAsync(
            new NetworkDiscoverySettings { ClientTimeoutMs = 300 }, staleSnapshot.RowVersion);
        await staleSave.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task CheckedSave_AbsentTelemetry_NeverPersistsClientHeartbeatAsync()
    {
        DateTime fabricated = DateTime.UtcNow;
        SettingsService creator = CreateService();
        SettingsSectionSnapshot created = await creator.SaveWithConcurrencyCheckAsync(
            new NetworkDiscoverySettings { LastHeartbeat = fabricated }, SettingsSectionSnapshot.AbsentRowVersion);
        ((NetworkDiscoverySettings)created.Value).LastHeartbeat.Should().BeNull();
        await AssertSectionHasNoHeartbeatAsync();

        SettingsSectionSnapshot updated = await CreateService().SaveWithConcurrencyCheckAsync(
            new NetworkDiscoverySettings { ClientTimeoutMs = 800, LastHeartbeat = fabricated }, created.RowVersion);

        NetworkDiscoverySettings updatedValue = (NetworkDiscoverySettings)updated.Value;
        updatedValue.ClientTimeoutMs.Should().Be(800);
        updatedValue.LastHeartbeat.Should().BeNull("telemetry is absent and client input is never authoritative");
        updatedValue.HeartbeatTelemetryUnreadable.Should().BeFalse();
        await AssertSectionHasNoHeartbeatAsync();
        NetworkDiscoverySettings reloaded = CreateService().Get<NetworkDiscoverySettings>();
        reloaded.ClientTimeoutMs.Should().Be(800);
        reloaded.LastHeartbeat.Should().BeNull("a checked save must not fabricate liveness for later reads");
        await using AppDbContext db = new(_options);
        (await db.AppSettingsEntities.AnyAsync(e => e.Key == NetworkDiscoverySettings.HeartbeatStorageKey))
            .Should().BeFalse("a settings save must not create heartbeat telemetry");
    }

    [Fact]
    public async Task CheckedSave_AbsentTelemetry_RetiresLegacyMirrorAsync()
    {
        DateTime legacy = DateTime.UtcNow.AddMinutes(-3);
        SeedSection(NetworkDiscoverySettings.SectionName, new NetworkDiscoverySettings { LastHeartbeat = legacy });
        SettingsService service = CreateService();
        service.Get<NetworkDiscoverySettings>().LastHeartbeat.Should().Be(legacy, "reads keep the pre-#2973 fallback until a save or heartbeat");
        string rowVersion = service.GetSectionSnapshot(NetworkDiscoverySettings.SectionName).RowVersion;

        SettingsSectionSnapshot saved = await service.SaveWithConcurrencyCheckAsync(
            new NetworkDiscoverySettings { ClientTimeoutMs = 600, LastHeartbeat = legacy }, rowVersion);

        ((NetworkDiscoverySettings)saved.Value).LastHeartbeat.Should().BeNull();
        await AssertSectionHasNoHeartbeatAsync();
        CreateService().Get<NetworkDiscoverySettings>().LastHeartbeat.Should().BeNull();
    }

    [Fact]
    public async Task CheckedSave_ValidTelemetry_ReturnsTelemetryWithoutMirroringItAsync()
    {
        SettingsSectionSnapshot seeded = await CreateService().SaveWithConcurrencyCheckAsync(
            new NetworkDiscoverySettings(), SettingsSectionSnapshot.AbsentRowVersion);
        DateTime heartbeat = DateTime.UtcNow.AddSeconds(-5);
        SeedTelemetry(JsonSerializer.Serialize(heartbeat));

        SettingsSectionSnapshot saved = await CreateService().SaveWithConcurrencyCheckAsync(
            new NetworkDiscoverySettings { LastHeartbeat = DateTime.UtcNow.AddDays(-1) }, seeded.RowVersion);

        ((NetworkDiscoverySettings)saved.Value).LastHeartbeat.Should().Be(heartbeat);
        await AssertSectionHasNoHeartbeatAsync();
        CreateService().Get<NetworkDiscoverySettings>().LastHeartbeat.Should().Be(heartbeat);
    }

    [Fact]
    public async Task BulkSave_AbsentTelemetry_NeverPersistsClientHeartbeatAsync()
    {
        DateTime fabricated = DateTime.UtcNow.AddYears(10);
        NetworkDiscoverySettings submitted = new() { ClientTimeoutMs = 800, LastHeartbeat = fabricated };
        SettingsService service = CreateService();

        service.Save(submitted);

        submitted.LastHeartbeat.Should().BeNull("telemetry is absent and client input is never authoritative");
        submitted.HeartbeatTelemetryUnreadable.Should().BeFalse();
        service.Get<NetworkDiscoverySettings>().LastHeartbeat.Should().BeNull();
        await AssertSectionHasNoHeartbeatAsync();
        NetworkDiscoverySettings reloaded = CreateService().Get<NetworkDiscoverySettings>();
        reloaded.ClientTimeoutMs.Should().Be(800);
        reloaded.LastHeartbeat.Should().BeNull("a bulk save must not fabricate liveness for later reads");
        reloaded.HeartbeatTelemetryUnreadable.Should().BeFalse();
        await using AppDbContext db = new(_options);
        (await db.AppSettingsEntities.AnyAsync(e => e.Key == NetworkDiscoverySettings.HeartbeatStorageKey))
            .Should().BeFalse("a settings save must not create heartbeat telemetry");
    }

    [Fact]
    public async Task BulkSave_AbsentTelemetry_RetiresLegacyMirrorAsync()
    {
        DateTime legacy = DateTime.UtcNow.AddMinutes(-3);
        SeedSection(NetworkDiscoverySettings.SectionName, new NetworkDiscoverySettings { LastHeartbeat = legacy });
        SettingsService service = CreateService();
        NetworkDiscoverySettings current = service.Get<NetworkDiscoverySettings>();
        current.LastHeartbeat.Should().Be(legacy);

        // Mirrors the apply-env endpoint, which re-saves the cached instance.
        service.Save(current);

        service.Get<NetworkDiscoverySettings>().LastHeartbeat.Should().BeNull();
        await AssertSectionHasNoHeartbeatAsync();
        CreateService().Get<NetworkDiscoverySettings>().LastHeartbeat.Should().BeNull();
    }

    [Fact]
    public async Task BulkSave_ValidTelemetry_CachesTelemetryWithoutMirroringItAsync()
    {
        DateTime heartbeat = DateTime.UtcNow.AddSeconds(-5);
        SeedTelemetry(JsonSerializer.Serialize(heartbeat));
        SettingsService service = CreateService();

        service.Save(new NetworkDiscoverySettings { LastHeartbeat = DateTime.UtcNow.AddYears(10) });

        service.Get<NetworkDiscoverySettings>().LastHeartbeat.Should().Be(heartbeat);
        await AssertSectionHasNoHeartbeatAsync();
        CreateService().Get<NetworkDiscoverySettings>().LastHeartbeat.Should().Be(heartbeat);
    }

    [Fact]
    public void BulkSave_CorruptTelemetry_ReportsUnknownLivenessWithoutMirroring()
    {
        SeedTelemetry("{not json");
        Mock<ILogger<SettingsService>> logger = new();
        SettingsService service = CreateService(logger.Object);
        logger.Invocations.Clear();

        service.Save(new NetworkDiscoverySettings { LastHeartbeat = DateTime.UtcNow });

        NetworkDiscoverySettings cached = service.Get<NetworkDiscoverySettings>();
        cached.LastHeartbeat.Should().BeNull();
        cached.HeartbeatTelemetryUnreadable.Should().BeTrue();
        VerifyTelemetryWarning(logger);
    }

    [Fact]
    public void Load_FutureLegacyMirror_ReportsUnknownLiveness()
    {
        SeedSection(NetworkDiscoverySettings.SectionName, new NetworkDiscoverySettings
        {
            ClientTimeoutMs = 700,
            LastHeartbeat = DateTime.UtcNow.AddYears(10),
        });
        Mock<ILogger<SettingsService>> logger = new();

        NetworkDiscoverySettings discovery = CreateService(logger.Object).Get<NetworkDiscoverySettings>();

        discovery.ClientTimeoutMs.Should().Be(700);
        discovery.LastHeartbeat.Should().BeNull("a far-future legacy mirror must not suppress staleness detection");
        discovery.HeartbeatTelemetryUnreadable.Should().BeTrue();
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains($"'{NetworkDiscoverySettings.SectionName}'")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Load_LegacyMirrorWithinClockSkewTolerance_IsAccepted()
    {
        DateTime legacy = DateTime.UtcNow.AddMinutes(1);
        SeedSection(NetworkDiscoverySettings.SectionName, new NetworkDiscoverySettings { LastHeartbeat = legacy });

        NetworkDiscoverySettings discovery = CreateService().Get<NetworkDiscoverySettings>();

        discovery.HeartbeatTelemetryUnreadable.Should().BeFalse();
        discovery.LastHeartbeat.Should().Be(legacy);
    }

    [Fact]
    public async Task HeartbeatAfterCorruption_RestoresLivenessWithoutChangingEditableRevisionAsync()
    {
        SettingsService seeder = CreateService();
        SettingsSectionSnapshot seeded = await seeder.SaveWithConcurrencyCheckAsync(
            new NetworkDiscoverySettings(), SettingsSectionSnapshot.AbsentRowVersion);
        SeedTelemetry("{not json");
        CreateService().Get<NetworkDiscoverySettings>().HeartbeatTelemetryUnreadable.Should().BeTrue();

        // Mirrors UnifiedSettingsController.SendHeartbeatAsync.
        DateTime heartbeat = DateTime.UtcNow;
        await using (AppDbContext db = new(_options))
        {
            EfAppSettingsRepository repository = new(db);
            await repository.SetAsync(NetworkDiscoverySettings.HeartbeatStorageKey, JsonSerializer.Serialize(heartbeat));
            await repository.SaveChangesAsync();
        }

        SettingsService reloaded = CreateService();
        NetworkDiscoverySettings discovery = reloaded.Get<NetworkDiscoverySettings>();
        discovery.HeartbeatTelemetryUnreadable.Should().BeFalse();
        discovery.LastHeartbeat.Should().Be(heartbeat);
        reloaded.GetSectionSnapshot(NetworkDiscoverySettings.SectionName).RowVersion.Should().Be(seeded.RowVersion);
    }

    [Fact]
    public async Task Monitor_UnreadableTelemetry_ReportsUnknownLivenessErrorAsync()
    {
        NetworkDiscoverySettings settings = new() { EnableDiscovery = true, HeartbeatTelemetryUnreadable = true };
        Mock<ISettingsService> settingsService = new();
        settingsService.Setup(s => s.GetByKey(NetworkDiscoverySettings.SectionName)).Returns(settings);
        ServiceCollection services = new();
        services.AddScoped(_ => settingsService.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();

        TaskCompletionSource<string> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<IBackgroundServiceMonitor> monitor = new();
        monitor.Setup(m => m.ReportError(DiscoveryHeartbeatMonitorService.ServiceId, It.IsAny<string>()))
            .Callback<string, string>((_, message) => reported.TrySetResult(message));
        using DiscoveryHeartbeatMonitorService service = new(
            monitor.Object, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DiscoveryHeartbeatMonitorService>.Instance);

        await service.StartAsync(CancellationToken.None);
        string message = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        message.Should().Contain("unreadable").And.Contain("unknown");
    }

    private static void VerifyTelemetryWarning(Mock<ILogger<SettingsService>> logger) =>
        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(NetworkDiscoverySettings.HeartbeatStorageKey)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

    private async Task AssertSectionHasNoHeartbeatAsync()
    {
        await using AppDbContext db = new(_options);
        AppSettingsEntity section = await db.AppSettingsEntities.AsNoTracking()
            .SingleAsync(e => e.Key == NetworkDiscoverySettings.SectionName);
        section.SettingsJson.Should().NotContain("lastHeartbeat");
    }

    private void SeedSection(string key, IAppSetting settings)
    {
        using AppDbContext db = new(_options);
        db.AppSettingsEntities.Add(new AppSettingsEntity
        {
            Key = key,
            SettingsJson = JsonSerializer.Serialize(settings, settings.GetType()),
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private void SeedTelemetry(string payload)
    {
        using AppDbContext db = new(_options);
        db.AppSettingsEntities.Add(new AppSettingsEntity
        {
            Key = NetworkDiscoverySettings.HeartbeatStorageKey,
            SettingsJson = payload,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private SettingsService CreateService(ILogger<SettingsService>? logger = null)
    {
        Mock<IDbContextFactory<AppDbContext>> factory = new();
        factory.Setup(f => f.CreateDbContext()).Returns(() => new AppDbContext(_options));
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));
        AppDbContext repositoryContext = new(_options);
        _repositoryContexts.Add(repositoryContext);
        return new SettingsService(new ConfigurationBuilder().Build(), factory.Object,
            logger ?? NullLogger<SettingsService>.Instance, new EfAppSettingsRepository(repositoryContext));
    }

    public void Dispose()
    {
        foreach (AppDbContext context in _repositoryContexts)
        {
            context.Dispose();
        }

        _connection.Dispose();
    }
}
