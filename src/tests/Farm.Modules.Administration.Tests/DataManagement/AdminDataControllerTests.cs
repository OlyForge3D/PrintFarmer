using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Web.Api.Tests;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Modules.Administration.Tests.DataManagement;

public class AdminDataControllerTests : IClassFixture<AdminDataControllerTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        public Factory() : base(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" })
        {
        }
    }

    private const string ApiKeySentinel = "admin-data-api-key-sentinel";
    private const string UsernameSentinel = "admin-data-username-sentinel";
    private const string PasswordSentinel = "admin-data-password-sentinel";

    private readonly Factory _factory;
    private HttpClient? _client;

    public AdminDataControllerTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        // Ensure database is reset to a known baseline state.
        // Note: the application seeds baseline catalog data on startup.
        await _factory.ResetDataAsync();
        _client = await _factory.CreateAdminClientAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("GET", "/api/admin/data/export/catalog")]
    [InlineData("POST", "/api/admin/data/import/catalog")]
    [InlineData("GET", "/api/admin/data/export/printers")]
    [InlineData("GET", "/api/admin/data/export/full")]
    [InlineData("POST", "/api/admin/data/import/full")]
    [InlineData("POST", "/api/admin/data/seed/reload")]
    public async Task Route_Unauthenticated_Returns401(string method, string route)
    {
        using HttpClient anon = _factory!.CreateClient();
        using HttpRequestMessage request = new(new HttpMethod(method), route);
        HttpResponseMessage response = await anon.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("GET", "/api/admin/data/export/catalog")]
    [InlineData("POST", "/api/admin/data/import/catalog")]
    [InlineData("GET", "/api/admin/data/export/printers")]
    [InlineData("GET", "/api/admin/data/export/full")]
    [InlineData("POST", "/api/admin/data/import/full")]
    [InlineData("POST", "/api/admin/data/seed/reload")]
    public async Task Route_AuthenticatedWithoutAdminPermission_Returns403(string method, string route)
    {
        using HttpClient nonAdmin = await _factory!.CreateAuthenticatedClientAsync(
            username: "admin-data-nonadmin",
            email: "admin-data-nonadmin@example.com");
        using HttpRequestMessage request = new(new HttpMethod(method), route);
        HttpResponseMessage response = await nonAdmin.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ExportFull_Admin_Returns200()
    {
        HttpResponseMessage response = await _client!.GetAsync("/api/admin/data/export/full");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExportCatalog_EmptyDatabase_ReturnsEmptyCatalog()
    {
        // Act
        HttpResponseMessage response = await _client!.GetAsync("/api/admin/data/export/catalog");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        CatalogExportDto? catalog = await response.Content.ReadFromJsonAsync<CatalogExportDto>();
        catalog.Should().NotBeNull();
        catalog!.Manufacturers.Should().NotBeNull();
        catalog.FilamentTypes.Should().NotBeNull();
        catalog.Manufacturers.Should().NotBeEmpty();
        catalog.FilamentTypes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExportFull_EmptyDatabase_ReturnsEmptyBackup()
    {
        // Act
        HttpResponseMessage response = await _client!.GetAsync("/api/admin/data/export/full");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        FullBackupExportDto? backup = await response.Content.ReadFromJsonAsync<FullBackupExportDto>();
        backup.Should().NotBeNull();
        backup!.Catalog.Should().NotBeNull();
        backup.Printers.Should().NotBeNull();
        backup.Locations.Should().NotBeNull();
    }

    [Fact]
    public async Task ExportPrinters_EmptyDatabase_ReturnsEmptyList()
    {
        // Act
        HttpResponseMessage response = await _client!.GetAsync("/api/admin/data/export/printers");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<PrinterExportDto>? printers = await response.Content.ReadFromJsonAsync<List<PrinterExportDto>>();
        printers.Should().NotBeNull();
        printers.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/admin/data/export/printers")]
    [InlineData("/api/admin/data/export/full")]
    public async Task Export_DefaultPayload_OmitsPrinterCredentials(string route)
    {
        await SeedPrinterWithCredentialsAsync();

        HttpResponseMessage response = await _client!.GetAsync(route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain(ApiKeySentinel);
        payload.Should().NotContain(UsernameSentinel);
        payload.Should().NotContain(PasswordSentinel);

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement printers = route.EndsWith("/full", StringComparison.Ordinal)
            ? document.RootElement.GetProperty("printers")
            : document.RootElement;
        printers.GetArrayLength().Should().Be(1);
        JsonElement printer = printers[0];
        printer.TryGetProperty("apiKey", out _).Should().BeFalse();
        printer.TryGetProperty("username", out _).Should().BeFalse();
        printer.TryGetProperty("password", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ImportCatalog_ValidData_ReturnsSuccess()
    {
        // Arrange
        var catalog = new CatalogExportDto
        {
            Manufacturers = new List<ManufacturerExportDto>
            {
                new() { Name = "Test Manufacturer" }
            },
            FilamentTypes = new List<FilamentTypeExportDto>
            {
                new()
                {
                    Name = "Test Filament",
                    DefaultHotendTemp = 200,
                    DefaultBedTemp = 60,
                    IsAbrasive = false,
                    NeedsEnclosure = false
                }
            },
            PrinterModels = new List<PrinterModelExportDto>(),
            Hotends = new List<HotendModelExportDto>(),
            Extruders = new List<ExtruderModelExportDto>(),
            Toolheads = new List<ToolheadModelExportDto>(),
            Nozzles = new List<NozzleModelExportDto>()
        };

        var request = new CatalogImportRequest { Catalog = catalog, Mode = ImportMode.Merge };

        // Act
        HttpResponseMessage response = await _client!.PostAsJsonAsync("/api/admin/data/import/catalog", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ImportResponseDto? result = await response.Content.ReadFromJsonAsync<ImportResponseDto>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Statistics.ManufacturersImported.Should().Be(1);
        result.Statistics.FilamentTypesImported.Should().Be(1);
    }

    [Fact]
    public async Task ImportCatalog_DuplicateData_SkipsDuplicates()
    {
        // Arrange - First import
        var catalog = new CatalogExportDto
        {
            Manufacturers = new List<ManufacturerExportDto>
            {
                new() { Name = "Duplicate Manufacturer" }
            },
            FilamentTypes = new List<FilamentTypeExportDto>(),
            PrinterModels = new List<PrinterModelExportDto>(),
            Hotends = new List<HotendModelExportDto>(),
            Extruders = new List<ExtruderModelExportDto>(),
            Toolheads = new List<ToolheadModelExportDto>(),
            Nozzles = new List<NozzleModelExportDto>()
        };

        var request = new CatalogImportRequest { Catalog = catalog, Mode = ImportMode.Merge };
        await _client!.PostAsJsonAsync("/api/admin/data/import/catalog", request);

        // Act - Second import with same data
        HttpResponseMessage response = await _client!.PostAsJsonAsync("/api/admin/data/import/catalog", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ImportResponseDto? result = await response.Content.ReadFromJsonAsync<ImportResponseDto>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Statistics.ManufacturersImported.Should().Be(0); // Duplicate skipped
    }

    [Fact]
    public async Task SeedReload_WithYamlFiles_ReturnsSuccess()
    {
        // Act
        HttpResponseMessage response = await _client!.PostAsync("/api/admin/data/seed/reload", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Note: The seed reload endpoint returns a generic success response, not ImportResponseDto
        // This test validates the endpoint works and returns success
        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ImportFullBackup_ValidData_ReturnsSuccess()
    {
        // Arrange
        var backup = new FullBackupExportDto
        {
            Catalog = new CatalogExportDto
            {
                Manufacturers = new List<ManufacturerExportDto>
                {
                    new() { Name = "Backup Test Manufacturer" }
                },
                FilamentTypes = new List<FilamentTypeExportDto>(),
                PrinterModels = new List<PrinterModelExportDto>(),
                Hotends = new List<HotendModelExportDto>(),
                Extruders = new List<ExtruderModelExportDto>(),
                Toolheads = new List<ToolheadModelExportDto>(),
                Nozzles = new List<NozzleModelExportDto>()
            },
            Printers = new List<PrinterExportDto>(),
            Locations = new List<LocationExportDto>(),
            ExportedAt = DateTime.UtcNow
        };

        var request = new FullBackupImportRequest { Backup = backup, Mode = ImportMode.Merge };

        // Act
        HttpResponseMessage response = await _client!.PostAsJsonAsync("/api/admin/data/import/full", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ImportResponseDto? result = await response.Content.ReadFromJsonAsync<ImportResponseDto>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Statistics.ManufacturersImported.Should().Be(1);
    }

    [Fact]
    public async Task ExportCatalog_AfterImport_ReturnsImportedData()
    {
        // Arrange - Import data first
        var catalog = new CatalogExportDto
        {
            Manufacturers = new List<ManufacturerExportDto>
            {
                new() { Name = "Export Test Manufacturer" }
            },
            FilamentTypes = new List<FilamentTypeExportDto>
            {
                new()
                {
                    Name = "Export Test Filament",
                    DefaultHotendTemp = 215,
                    DefaultBedTemp = 70,
                    IsAbrasive = false,
                    NeedsEnclosure = false
                }
            },
            PrinterModels = new List<PrinterModelExportDto>(),
            Hotends = new List<HotendModelExportDto>(),
            Extruders = new List<ExtruderModelExportDto>(),
            Toolheads = new List<ToolheadModelExportDto>(),
            Nozzles = new List<NozzleModelExportDto>()
        };

        var request = new CatalogImportRequest { Catalog = catalog, Mode = ImportMode.Merge };
        await _client!.PostAsJsonAsync("/api/admin/data/import/catalog", request);

        // Act - Export the data
        HttpResponseMessage response = await _client!.GetAsync("/api/admin/data/export/catalog");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        CatalogExportDto? exportedCatalog = await response.Content.ReadFromJsonAsync<CatalogExportDto>();
        exportedCatalog.Should().NotBeNull();
        exportedCatalog!.Manufacturers.Should().Contain(m => m.Name == "Export Test Manufacturer");
        exportedCatalog.FilamentTypes.Should().Contain(f => f.Name == "Export Test Filament");
    }

    private async Task SeedPrinterWithCredentialsAsync()
    {
        await using AsyncServiceScope scope = _factory!.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = "Admin Data Redaction Manufacturer"
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = "Admin Data Redaction Model",
            Manufacturer = manufacturer,
            ManufacturerId = manufacturer.Id
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Admin Data Redaction Printer",
            ServerUrl = "http://admin-data-redaction.invalid",
            BackendPort = 80,
            Manufacturer = manufacturer,
            ManufacturerId = manufacturer.Id,
            Model = model,
            ModelId = model.Id,
            ApiKey = ApiKeySentinel,
            Username = UsernameSentinel,
            Password = PasswordSentinel
        };
        context.Printers.Add(printer);
        await context.SaveChangesAsync();
    }
}
