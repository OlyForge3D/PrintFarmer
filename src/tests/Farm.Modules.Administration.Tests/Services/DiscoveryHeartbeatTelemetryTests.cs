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
/// settings loading or checked saves, and must be reported as unknown liveness.
/// </summary>
public sealed class DiscoveryHeartbeatTelemetryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

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
        return new SettingsService(new ConfigurationBuilder().Build(), factory.Object,
            logger ?? NullLogger<SettingsService>.Instance, new Mock<IAppSettingsRepository>(MockBehavior.Strict).Object);
    }

    public void Dispose() => _connection.Dispose();
}
