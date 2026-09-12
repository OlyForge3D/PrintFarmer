using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Background;
using Farm.Infrastructure.Services.StorageManagement;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for the <c>/api/system/info</c> endpoint.
/// </summary>
public class SystemInfoIntegrationTests : IClassFixture<SystemInfoIntegrationTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        public Factory()
            : base(new Dictionary<string, string?>
            {
                ["Security:DevModeBypassAuth"] = "false",
            })
        {
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
    public async Task GetInfo_NonAdminAfterAdminCacheWarmup_StillDeniesInventory()
    {
        (await _adminClient!.GetAsync("/api/system/info")).StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage denied = await _nonAdminClient!.GetAsync("/api/system/info");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).Should().NotContain("platformDigest").And.NotContain("sourceCommit");
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
