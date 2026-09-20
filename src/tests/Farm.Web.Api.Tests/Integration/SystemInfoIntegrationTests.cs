using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Services.SystemStatus;
using Farm.Slicer.Module.Services.SystemInfo;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for the <c>/api/system/info</c> endpoint.
/// </summary>
public class SystemInfoIntegrationTests : IClassFixture<SystemInfoIntegrationTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        private readonly bool _throwDiscoveryOptions;
        private readonly bool _throwSchedulingStatus;

        public Factory()
            : this(discoveryEnabled: true, throwDiscoveryOptions: false, throwSchedulingStatus: false)
        {
        }

        private Factory(bool discoveryEnabled, bool throwDiscoveryOptions, bool throwSchedulingStatus)
            : base(new Dictionary<string, string?>
            {
                ["Security:DevModeBypassAuth"] = "false",
                ["HostUpdates:VerifiedReleaseDiscovery:Enabled"] = discoveryEnabled.ToString(),
            })
        {
            _throwDiscoveryOptions = throwDiscoveryOptions;
            _throwSchedulingStatus = throwSchedulingStatus;
        }

        public static Factory WithDiscoveryDisabled() =>
            new(discoveryEnabled: false, throwDiscoveryOptions: false, throwSchedulingStatus: false);

        public static Factory WithInvalidDiscoveryOptions() =>
            new(discoveryEnabled: true, throwDiscoveryOptions: true, throwSchedulingStatus: false);

        public static Factory WithThrowingSchedulingStatusProvider() =>
            new(discoveryEnabled: true, throwDiscoveryOptions: false, throwSchedulingStatus: true);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (_throwSchedulingStatus)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostUpdateSchedulingStatusProvider>();
                    services.AddScoped<IHostUpdateSchedulingStatusProvider, ThrowingHostUpdateSchedulingStatusProvider>();
                });
            }

            if (!_throwDiscoveryOptions)
            {
                return;
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IOptionsMonitor<VerifiedReleaseDiscoveryOptions>>();
                services.AddSingleton<IOptionsMonitor<VerifiedReleaseDiscoveryOptions>>(
                    new ThrowingVerifiedReleaseDiscoveryOptionsMonitor());
            });
        }

        private sealed class ThrowingHostUpdateSchedulingStatusProvider : IHostUpdateSchedulingStatusProvider
        {
            public HostUpdateSchedulingStatusDto? GetStatus() =>
                throw new InvalidOperationException("scheduling_status_provider_failed");
        }
    }

    private readonly Factory _factory;
    private HttpClient? _adminClient;
    private HttpClient? _nonAdminClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public SystemInfoIntegrationTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
        _factory.Services.GetRequiredService<IMemoryCache>().Remove("SystemInfo:Snapshot");
        _adminClient = await _factory.CreateAdminClientAsync();
        _nonAdminClient = await _factory.CreateAuthenticatedClientAsync(
            username: "system-info-user",
            email: "system-info-user@example.com");

        await SeedSystemInfoDataAsync();
    }

    public Task DisposeAsync()
    {
        _adminClient?.Dispose();
        _nonAdminClient?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InventorySources_HostDiResolvesModuleImplementationsAndPreservesApiBuild()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IServiceInventorySource[] sources = scope.ServiceProvider.GetServices<IServiceInventorySource>().ToArray();
        sources.Should().HaveCount(2);
        LocalServiceInventorySource local = sources.OfType<LocalServiceInventorySource>().Single();
        SlicerServiceInventorySource slicer = sources.OfType<SlicerServiceInventorySource>().Single();
        Assert.Same(typeof(ISystemInfoService).Assembly, local.GetType().Assembly);
        Assert.Same(typeof(SlicerDbContext).Assembly, slicer.GetType().Assembly);

        IReadOnlyList<ServiceReplicaObservationDto> rows = await local.ReadAsync(CancellationToken.None);
        ServiceReplicaObservationDto api = rows.Single(row => row.Component == "api");
        (string? version, string? commit) = ApplicationBuildObservation.FromAssembly(typeof(Program).Assembly);
        api.ApplicationVersion.Should().Be(version);
        api.SourceCommit.Should().Be(commit);
    }

    [Fact]
    public async Task GetInfo_Admin_ReportsAutomaticUpdateSchedulingFromTheRegisteredProvider()
    {
        HttpResponseMessage response = await _adminClient!.GetAsync("/api/system/info");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        SystemInfoDto? dto = await response.Content.ReadFromJsonAsync<SystemInfoDto>(JsonOptions);

        // The scheduling status provider is registered in production DI, so an unprovisioned
        // host reports an explicit "registered but not running" status rather than null.
        dto!.UpdateScheduling.Should().NotBeNull();
        dto.UpdateScheduling!.ConfiguredEnabled.Should().BeFalse();
        dto.UpdateScheduling.EffectiveEnabled.Should().BeFalse();
        dto.UpdateScheduling.EffectiveChannel.Should().BeNull();
        dto.UpdateScheduling.Executor.State.Should().Be(HostUpdateExecutorState.Unavailable);
        dto.UpdateScheduling.Executor.Reason.Should().Be(HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason);
        dto.UpdateScheduling.Backoff.State.Should().Be(HostUpdateBackoffState.Unknown);
        dto.UpdateScheduling.Reasons.Should().Equal(
            "host_update_policy_repository_not_available",
            "host_update_replay_anchor_not_available",
            "host_update_replay_store_not_available",
            HostUpdateSchedulingAvailability.AdmissionFenceReason,
            HostUpdateSchedulingAvailability.ExecutorNotProvisionedReason);
        dto.UpdateScheduling.KillSwitch.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetInfo_Admin_SurvivesAFailingSchedulingStatusProvider()
    {
        using Factory factory = Factory.WithThrowingSchedulingStatusProvider();
        using HttpClient adminClient = await factory.CreateAdminClientAsync();
        factory.Services.GetRequiredService<IMemoryCache>().Remove("SystemInfo:Snapshot");

        HttpResponseMessage response = await adminClient.GetAsync("/api/system/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SystemInfoDto? dto = await response.Content.ReadFromJsonAsync<SystemInfoDto>(JsonOptions);
        dto!.UpdateScheduling.Should().BeNull();
        dto.App.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetInfo_Unauthenticated_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync("/api/system/info");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetInfo_NonAdminRole_Returns403()
    {
        HttpResponseMessage response = await _nonAdminClient!.GetAsync("/api/system/info");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetInfo_Admin_ReturnsExpectedShapeAndCounts()
    {
        HttpResponseMessage response = await _adminClient!.GetAsync("/api/system/info");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        SystemInfoDto? dto = await response.Content.ReadFromJsonAsync<SystemInfoDto>(JsonOptions);
        dto.Should().NotBeNull();
        dto!.App.Version.Should().NotBeNullOrWhiteSpace();
        dto.Inventory!.HostUpdaterVersion.Should().Be(dto.App.Version);
        HostUpdateValidation.IsSemanticVersion(dto.Inventory.HostUpdaterVersion).Should().BeTrue();
        dto.App.Uptime.Should().NotBeNullOrWhiteSpace();
        dto.App.Hostname.Should().NotBeNullOrWhiteSpace();
        dto.Cpu.Cores.Should().BeGreaterThan(0);
        dto.Cpu.UsagePercent.Should().BeGreaterThanOrEqualTo(0);
        dto.Memory.UsedBytes.Should().BeGreaterThanOrEqualTo(0);
        dto.Memory.TotalBytes.Should().BeGreaterThanOrEqualTo(0);
        dto.Disk.UsedBytes.Should().BeGreaterThanOrEqualTo(0);
        dto.Disk.TotalBytes.Should().BeGreaterThanOrEqualTo(0);
        dto.Disk.ArchiveBytes.Should().BeGreaterThan(0);
        dto.Disk.DatabaseBytes.Should().BeGreaterThanOrEqualTo(0);
        dto.Database.Engine.Should().Be("SQLite");
        dto.Database.Version.Should().NotBeNullOrWhiteSpace();
        dto.Database.PrinterCount.Should().Be(1);
        dto.Database.ArchiveCount.Should().Be(1);
        dto.Services.Should().NotBeEmpty();
        dto.Services.Should().Contain(service => service.Name == "Backend API" && service.Health == SystemServiceHealth.Healthy);
    }

    [Fact]
    public async Task GetInfo_Admin_SerializesHealthEnumAsString()
    {
        HttpResponseMessage response = await _adminClient!.GetAsync("/api/system/info");
        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument document = JsonDocument.Parse(json);
        string? health = document.RootElement
            .GetProperty("services")[0]
            .GetProperty("health")
            .GetString();

        health.Should().Be("Healthy");
    }

    [Fact]
    public async Task GetInfo_Admin_OmitsDisabledBackgroundServices()
    {
        const string serviceId = "DisabledSystemInfoTestService";
        const string displayName = "Disabled System Info Test Service";

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            IBackgroundServiceMonitor monitor = scope.ServiceProvider.GetRequiredService<IBackgroundServiceMonitor>();
            monitor.Register(serviceId, displayName);
            monitor.ReportStarted(serviceId);
            monitor.ReportEnabled(serviceId, false);
        }

        HttpResponseMessage response = await _adminClient!.GetAsync("/api/system/info");
        SystemInfoDto? dto = await response.Content.ReadFromJsonAsync<SystemInfoDto>(JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        dto.Should().NotBeNull();
        dto!.Services.Should().NotContain(service => service.Name == displayName);
    }

    [Fact]
    public async Task GetInfo_Admin_ReportsUnknownProvenanceAndExplicitNullsWithoutChangingLegacyShape()
    {
        HttpResponseMessage response = await _adminClient!.GetAsync("/api/system/info");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement inventory = json.RootElement.GetProperty("inventory");
        inventory.GetProperty("selectedChannel").GetString().Should().Be("stable");
        inventory.GetProperty("observedChannel").ValueKind.Should().Be(JsonValueKind.Null);
        inventory.GetProperty("targetChannel").ValueKind.Should().Be(JsonValueKind.Null);
        JsonElement api = inventory.GetProperty("services").EnumerateArray().Single(row => row.GetProperty("component").GetString() == "api");
        api.GetProperty("applicationVersion").GetString().Should().NotBeNullOrWhiteSpace();
        api.GetProperty("identity").ValueKind.Should().Be(JsonValueKind.Null);
        api.GetProperty("platformDigest").ValueKind.Should().Be(JsonValueKind.Null);
        api.GetProperty("indexDigest").ValueKind.Should().Be(JsonValueKind.Null);
        api.GetProperty("manifestDigest").ValueKind.Should().Be(JsonValueKind.Null);
        api.GetProperty("databaseProvider").ValueKind.Should().Be(JsonValueKind.Null);
        api.GetProperty("migrationHead").ValueKind.Should().Be(JsonValueKind.Null);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        json.RootElement.GetProperty("services")[0].GetProperty("version").ValueKind.Should().Be(JsonValueKind.String);
        json.RootElement.GetProperty("database").GetProperty("migrationHeads").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetInfo_Admin_ProjectsAllReplicasWithoutSecretsOrEngineAsBuild()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            db.SlicerServices.AddRange(
                new SlicerService { Id = first, Name = "first", Version = "2.4.2", Host = "http://private-worker.invalid", ApiKey = "never-return-registry-key", Status = "Online", LastSeen = DateTime.UtcNow,
                    CapabilitiesJson = "{\"applicationBuild\":\"1.2.3\",\"slicerContainerDigest\":\"not-attestation\"}" },
                new SlicerService { Id = second, Name = "second", Version = "2.4.2", Host = "http://private-worker.invalid", Status = "Offline", LastSeen = DateTime.UtcNow.AddHours(-1) });
            await db.SaveChangesAsync();
        }

        string json = await _adminClient!.GetStringAsync("/api/system/info");
        SystemInfoDto dto = JsonSerializer.Deserialize<SystemInfoDto>(json, JsonOptions)!;
        ServiceReplicaObservationDto[] workers = dto.Inventory!.Services.Where(row => row.Component == "slicer-worker").ToArray();
        workers.Should().Contain(row => row.InstanceId == first.ToString() && row.ApplicationVersion == "1.2.3" && row.EngineVersion == "2.4.2");
        workers.Should().Contain(row => row.InstanceId == second.ToString() && row.ApplicationVersion == null && row.ObservationState == InventoryObservationState.Unavailable);
        workers.Should().OnlyContain(row => row.PlatformDigest == null && row.Identity == null);
        json.Should().NotContain("private-worker").And.NotContain("never-return-registry-key").And.NotContain("not-attestation").And.NotContain("capabilitiesJson");
    }

    [Fact]
    public async Task GetInfo_Admin_ReadinessReflectsVerifiedReleaseEvidenceCacheInIsolatedHost()
    {
        await using Factory isolatedFactory = new();
        await isolatedFactory.ResetDataAsync();
        using HttpClient isolatedAdmin = await isolatedFactory.CreateAdminClientAsync();

        HttpResponseMessage before = await isolatedAdmin.GetAsync("/api/system/info");
        using (JsonDocument beforeJson = JsonDocument.Parse(await before.Content.ReadAsStringAsync()))
        {
            JsonElement readiness = beforeJson.RootElement.GetProperty("inventory").GetProperty("readiness");
            readiness.GetProperty("state").GetString().Should().Be("Unknown");
            readiness.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).Should().Contain("VerifiedReleaseEvidenceUnavailable");
        }

        // A release whose channel does not match the host's selected channel ("stable" by
        // default) is unambiguously Blocked, proving the evaluator is wired against a
        // *populated* cache, not always the "no evidence" branch.
        IVerifiedReleaseEvidenceCache cache =
            isolatedFactory.Services.GetRequiredService<IVerifiedReleaseEvidenceCache>();
        cache.SetVerified(
            new VerifiedReleaseEvidenceDto
            {
                Sequence = 999_999,
                MinimumUpdaterVersion = "1.0.0",
                SignatureVerified = true,
                IsComplete = true,
                ManifestDigest = "sha256:" + new string('a', 64),
                Identity = new CanonicalReleaseIdentityDto
                {
                    CanonicalVersion = "9.9.9",
                    BaseVersion = "9.9.9",
                    Channel = "insider",
                    ReleaseId = "insider:9.9.9",
                },
                Services = [],
            },
            DateTimeOffset.UtcNow);
        isolatedFactory.Services.GetRequiredService<IMemoryCache>().Remove("SystemInfo:Snapshot");

        HttpResponseMessage after = await isolatedAdmin.GetAsync("/api/system/info");
        using JsonDocument afterJson = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        afterJson.RootElement.GetProperty("inventory").GetProperty("readiness").GetProperty("state").GetString().Should().Be("Blocked");
    }

    [Fact]
    public async Task GetInfo_Admin_DiscoveryFailureRevokesPriorReadinessWithoutDiscardingDiagnostics()
    {
        await using Factory isolatedFactory = new();
        await isolatedFactory.ResetDataAsync();
        using HttpClient isolatedAdmin = await isolatedFactory.CreateAdminClientAsync();
        IVerifiedReleaseEvidenceCache cache =
            isolatedFactory.Services.GetRequiredService<IVerifiedReleaseEvidenceCache>();
        VerifiedReleaseEvidenceDto evidence = new()
        {
            Sequence = 3,
            MinimumUpdaterVersion = "1.0.0",
            SignatureVerified = true,
            IsComplete = true,
            ManifestDigest = "sha256:" + new string('a', 64),
            Identity = new CanonicalReleaseIdentityDto
            {
                CanonicalVersion = "0.0.0",
                BaseVersion = "0.0.0",
                Channel = "stable",
                ReleaseId = "stable:0.0.0",
            },
        };
        DateTimeOffset verifiedAt = DateTimeOffset.UtcNow;
        cache.SetVerified(evidence, verifiedAt);
        cache.SetVerified(evidence with { Sequence = 2 }, verifiedAt.AddMinutes(1)).Should().BeFalse();
        const string rollbackMessage = "Rejected rollback release for channel 'stable' (sequence=2).";
        cache.SetError(rollbackMessage);

        HttpResponseMessage response = await isolatedAdmin.GetAsync("/api/system/info");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement readiness = json.RootElement.GetProperty("inventory").GetProperty("readiness");

        readiness.GetProperty("state").GetString().Should().Be("Unknown");
        readiness.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString())
            .Should().Contain("VerifiedReleaseDiscoveryFailed");
        cache.Current.Should().BeSameAs(evidence);
        cache.Current!.Sequence.Should().Be(3);
        cache.LastVerifiedAt.Should().Be(verifiedAt);
        cache.LastError.Should().Be(rollbackMessage);
    }

    [Fact]
    public async Task GetInfo_Admin_InvalidDiscoveryOptionsReportsReadinessUnavailable()
    {
        await using Factory isolatedFactory = Factory.WithInvalidDiscoveryOptions();
        await isolatedFactory.ResetDataAsync();
        using HttpClient isolatedAdmin = await isolatedFactory.CreateAdminClientAsync();
        isolatedFactory.Services.GetRequiredService<IMemoryCache>().Remove("SystemInfo:Snapshot");

        HttpResponseMessage response = await isolatedAdmin.GetAsync("/api/system/info");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement readiness = json.RootElement.GetProperty("inventory").GetProperty("readiness");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        readiness.GetProperty("state").GetString().Should().Be("Unknown");
        readiness.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString())
            .Should().Contain("VerifiedReleaseDiscoveryOptionsInvalid");
    }

    [Theory]
    [MemberData(nameof(NormalizeAssemblyVersionCases))]
    public void NormalizeAssemblyVersion_ProducesExpectedSemanticVersion(Version? assemblyVersion, string expected)
    {
        string normalized = SystemInfoService.NormalizeAssemblyVersion(assemblyVersion);

        normalized.Should().Be(expected);
        HostUpdateValidation.IsSemanticVersion(normalized).Should().BeTrue();
    }

    // Build == -1 (no third component, e.g. `new Version(1, 2)`) previously fell through to Math.Max, but this
    // case must stay covered explicitly so a regression there fails a test instead of only field observation.
    public static TheoryData<Version?, string> NormalizeAssemblyVersionCases => new()
    {
        { new Version(1, 2, 3, 4), "1.2.3" },
        { new Version(1, 2), "1.2.0" },
        { null, "0.0.0" },
    };

    private sealed class ThrowingVerifiedReleaseDiscoveryOptionsMonitor : IOptionsMonitor<VerifiedReleaseDiscoveryOptions>
    {
        public VerifiedReleaseDiscoveryOptions CurrentValue => throw new OptionsValidationException(
            Options.DefaultName,
            typeof(VerifiedReleaseDiscoveryOptions),
            ["invalid interval"]);

        public VerifiedReleaseDiscoveryOptions Get(string? name) => new();

        public IDisposable? OnChange(Action<VerifiedReleaseDiscoveryOptions, string?> listener) => null;
    }

    [Fact]
    public async Task GetInfo_Admin_DisabledDiscoveryRevokesPriorReadiness()
    {
        await using Factory isolatedFactory = Factory.WithDiscoveryDisabled();
        await isolatedFactory.ResetDataAsync();
        using HttpClient isolatedAdmin = await isolatedFactory.CreateAdminClientAsync();
        IVerifiedReleaseEvidenceCache cache =
            isolatedFactory.Services.GetRequiredService<IVerifiedReleaseEvidenceCache>();
        cache.SetVerified(
            new VerifiedReleaseEvidenceDto
            {
                Sequence = 99_999,
                MinimumUpdaterVersion = "1.0.0",
                SignatureVerified = true,
                IsComplete = true,
                ManifestDigest = "sha256:" + new string('a', 64),
                Identity = new CanonicalReleaseIdentityDto
                {
                    CanonicalVersion = "0.0.0",
                    BaseVersion = "0.0.0",
                    Channel = "stable",
                    ReleaseId = "stable:0.0.0",
                },
            },
            DateTimeOffset.UtcNow);

        HttpResponseMessage response = await isolatedAdmin.GetAsync("/api/system/info");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement readiness = json.RootElement.GetProperty("inventory").GetProperty("readiness");

        readiness.GetProperty("state").GetString().Should().Be("Unknown");
        readiness.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString())
            .Should().Contain("VerifiedReleaseDiscoveryDisabled");
    }

    [Fact]
    public async Task GetInfo_Admin_StaleVerifiedReleaseEvidenceIsNotEligible()
    {
        await using Factory isolatedFactory = new();
        await isolatedFactory.ResetDataAsync();
        using HttpClient isolatedAdmin = await isolatedFactory.CreateAdminClientAsync();
        IVerifiedReleaseEvidenceCache cache =
            isolatedFactory.Services.GetRequiredService<IVerifiedReleaseEvidenceCache>();
        cache.SetVerified(
            new VerifiedReleaseEvidenceDto
            {
                Sequence = 99_999,
                MinimumUpdaterVersion = "1.0.0",
                SignatureVerified = true,
                IsComplete = true,
                ManifestDigest = "sha256:" + new string('a', 64),
                Identity = new CanonicalReleaseIdentityDto
                {
                    CanonicalVersion = "0.0.0",
                    BaseVersion = "0.0.0",
                    Channel = "stable",
                    ReleaseId = "stable:0.0.0",
                },
            },
            DateTimeOffset.UtcNow.AddHours(-3));

        HttpResponseMessage response = await isolatedAdmin.GetAsync("/api/system/info");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement readiness = json.RootElement.GetProperty("inventory").GetProperty("readiness");

        readiness.GetProperty("state").GetString().Should().Be("Unknown");
        readiness.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString())
            .Should().Contain("VerifiedReleaseEvidenceStale");
    }

    [Fact]
    public async Task GetInfo_NonAdminAfterAdminCacheWarmup_StillDeniesInventory()
    {
        (await _adminClient!.GetAsync("/api/system/info")).StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage denied = await _nonAdminClient!.GetAsync("/api/system/info");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).Should().NotContain("platformDigest").And.NotContain("sourceCommit");
    }

    [Fact]
    public async Task GetInfo_CustomRoleWithExactAdminPermission_ReturnsInventory()
    {
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User user = await db.Users.SingleAsync(row => row.Username == "system-info-user");
            Resource resource = await db.Resources.SingleAsync(row => row.Name == "system_settings");
            UserAction action = await db.UserActions.SingleAsync(row => row.Name == "admin");
            Role role = new() { Id = Guid.NewGuid(), Name = "inventory-reader", DisplayName = "Inventory reader", IsActive = true };
            db.Roles.Add(role);
            db.RolePermissions.Add(new RolePermission { Id = Guid.NewGuid(), RoleId = role.Id, ResourceId = resource.Id, ActionId = action.Id, Granted = true });
            db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = role.Id, IsActive = true, AssignedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using HttpClient customAdmin = await _factory.CreateAuthenticatedClientAsync("system-info-user", "system-info-user@example.com");
        HttpResponseMessage response = await customAdmin.GetAsync("/api/system/info");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("platformDigest");
    }

    [Fact]
    public async Task GetInfo_ProductionSignalRSerializer_MatchesRestInventoryContract()
    {
        string rest = await _adminClient!.GetStringAsync("/api/system/info");
        SystemInfoDto dto = JsonSerializer.Deserialize<SystemInfoDto>(rest, JsonOptions)!;
        var options = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.SignalR.JsonHubProtocolOptions>>();
        string signalR = JsonSerializer.Serialize(dto.Inventory, options.Value.PayloadSerializerOptions);
        using JsonDocument restJson = JsonDocument.Parse(rest);
        using JsonDocument signalRJson = JsonDocument.Parse(signalR);
        JsonElement.DeepEquals(restJson.RootElement.GetProperty("inventory"), signalRJson.RootElement).Should().BeTrue();
    }

    private async Task SeedSystemInfoDataAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IStoragePathService storagePathService = scope.ServiceProvider.GetRequiredService<IStoragePathService>();

        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = "System Info Manufacturer",
        };

        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            Name = "System Info Model",
            ManufacturerId = manufacturer.Id,
        };

        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "System Info Printer",
            ServerUrl = "http://printer.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };

        FolderNode folder = await db.Set<FolderNode>()
            .FirstAsync(node => node.Path == "/" && node.FolderType == "gcode");

        string storageDirectory = storagePathService.GetGcodeStorageDirectory();
        Directory.CreateDirectory(storageDirectory);
        string filePath = Path.Join(storageDirectory, "system-info-sample.gcode");
        await File.WriteAllTextAsync(filePath, "; generated for system info integration test", CancellationToken.None);
        long fileSizeBytes = new FileInfo(filePath).Length;

        GcodeFile gcodeFile = new()
        {
            Id = Guid.NewGuid(),
            Name = "system-info-sample.gcode",
            FileName = "system-info-sample.gcode",
            FolderId = folder.Id,
            FilePath = filePath,
            FileHash = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant(),
            FileSizeBytes = fileSizeBytes,
            UploadedAt = DateTime.UtcNow,
            Source = GcodeSource.Upload,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.Printers.Add(printer);
        db.GcodeFiles.Add(gcodeFile);
        await db.SaveChangesAsync();
    }
}
