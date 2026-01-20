using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Models.Admin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.DataManagement;

public class AdminDataControllerTests : IAsyncLifetime
{
    private CustomWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        // Create a fresh factory for each test to ensure database isolation
        _factory = new CustomWebApplicationFactory();
        _client = _factory.CreateClient();
        
        // Ensure database is reset to empty state
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExportCatalog_EmptyDatabase_ReturnsEmptyCatalog()
    {
        // Act
        var response = await _client!.GetAsync("/api/admin/data/export/catalog");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var catalog = await response.Content.ReadFromJsonAsync<CatalogExportDto>();
        catalog.Should().NotBeNull();
        catalog!.Manufacturers.Should().BeEmpty();
        catalog.FilamentTypes.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportFull_EmptyDatabase_ReturnsEmptyBackup()
    {
        // Act
        var response = await _client.GetAsync("/api/admin/data/export/full");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var backup = await response.Content.ReadFromJsonAsync<FullBackupExportDto>();
        backup.Should().NotBeNull();
        backup!.Catalog.Should().NotBeNull();
        backup.Printers.Should().NotBeNull();
        backup.Locations.Should().NotBeNull();
    }

    [Fact]
    public async Task ExportPrinters_EmptyDatabase_ReturnsEmptyList()
    {
        // Act
        var response = await _client.GetAsync("/api/admin/data/export/printers");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var printers = await response.Content.ReadFromJsonAsync<List<PrinterExportDto>>();
        printers.Should().NotBeNull();
        printers.Should().BeEmpty();
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

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/data/import/catalog", catalog);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ImportResponseDto>();
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

        await _client.PostAsJsonAsync("/api/admin/data/import/catalog", catalog);

        // Act - Second import with same data
        var response = await _client.PostAsJsonAsync("/api/admin/data/import/catalog", catalog);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ImportResponseDto>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Statistics.ManufacturersImported.Should().Be(0); // Duplicate skipped
    }

    [Fact]
    public async Task SeedReload_WithYamlFiles_ReturnsSuccess()
    {
        // Act
        var response = await _client.PostAsync("/api/admin/data/seed/reload", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ImportResponseDto>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();

        // Should have imported manufacturers from YAML files
        result.Statistics.ManufacturersImported.Should().BeGreaterThan(0);
        result.Statistics.FilamentTypesImported.Should().BeGreaterThan(0);
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

        // Act
        var response = await _client.PostAsJsonAsync("/api/admin/data/import/full", backup);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ImportResponseDto>();
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

        await _client.PostAsJsonAsync("/api/admin/data/import/catalog", catalog);

        // Act - Export the data
        var response = await _client.GetAsync("/api/admin/data/export/catalog");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportedCatalog = await response.Content.ReadFromJsonAsync<CatalogExportDto>();
        exportedCatalog.Should().NotBeNull();
        exportedCatalog!.Manufacturers.Should().Contain(m => m.Name == "Export Test Manufacturer");
        exportedCatalog.FilamentTypes.Should().Contain(f => f.Name == "Export Test Filament");
    }
}
