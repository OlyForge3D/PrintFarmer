using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.StorageManagement;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for the <c>/api/system/info</c> endpoint.
/// </summary>
public class SystemInfoIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _adminClient;
    private HttpClient? _nonAdminClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public SystemInfoIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
        });
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _adminClient = await _factory.CreateAdminClientAsync();
        _nonAdminClient = await _factory.CreateAuthenticatedClientAsync(
            username: "system-info-user",
            email: "system-info-user@example.com");

        await SeedSystemInfoDataAsync();
    }

    public async Task DisposeAsync()
    {
        _adminClient?.Dispose();
        _nonAdminClient?.Dispose();
        await _factory.DisposeAsync();
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
        string filePath = Path.Combine(storageDirectory, "system-info-sample.gcode");
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
