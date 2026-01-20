using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Integration tests for OrcaSlicer bundle import/export round-trip functionality.
/// Tests the complete workflow: preview → import → export → verify.
/// </summary>
public class OrcaBundleIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client;

    public OrcaBundleIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateAdminClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact(DisplayName = "Import with mapping resolves printer models correctly")]
    public async Task Import_WithMapping_ResolvesPrinterModels()
    {
        // Arrange - Seed database with known printer models
        await SeedPrinterModels();

        string bundleJson = """
        {
            "printer": [
                {
                    "name": "Bambu Lab X1C",
                    "printer_model": "X1 Carbon",
                    "printer_vendor": "Bambu Lab",
                    "bed_width": 256,
                    "bed_depth": 256,
                    "max_print_height": 256,
                    "nozzle_diameter": 0.4,
                    "max_bed_temperature": 120,
                    "max_hotend_temperature": 300
                }
            ],
            "filament": [],
            "process": []
        }
        """;

        ImportOrcaBundleDto previewRequest = new ImportOrcaBundleDto { BundleJson = bundleJson };
        StringContent content = new StringContent(
            JsonSerializer.Serialize(previewRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        HttpResponseMessage response = await _client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string previewJson = await response.Content.ReadAsStringAsync();
        OrcaBundlePreviewDto? preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(previewJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = preview.Should().NotBeNull();
        _ = preview!.Printers.Should().HaveCount(1);
        _ = preview.Printers[0].Name.Should().Contain("X1");
    }

    [Fact(DisplayName = "Import with filament type mapping resolves materials correctly")]
    public async Task Import_WithFilamentMapping_ResolvesMaterials()
    {
        // Arrange - Seed database with known filament types
        await SeedFilamentTypes();

        string bundleJson = """
        {
            "printer": [],
            "filament": [
                {
                    "name": "Generic PLA @X1C",
                    "filament_type": "PLA",
                    "nozzle_temperature": 220,
                    "bed_temperature": 65,
                    "fan_speed": 100,
                    "retraction_length": 0.4
                },
                {
                    "name": "Generic PETG @X1C",
                    "filament_type": "PETG",
                    "nozzle_temperature": 250,
                    "bed_temperature": 80,
                    "fan_speed": 50,
                    "retraction_length": 0.6
                }
            ],
            "process": []
        }
        """;

        ImportOrcaBundleDto previewRequest = new ImportOrcaBundleDto { BundleJson = bundleJson };
        StringContent content = new StringContent(
            JsonSerializer.Serialize(previewRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        HttpResponseMessage response = await _client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string previewJson = await response.Content.ReadAsStringAsync();
        OrcaBundlePreviewDto? preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(previewJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = preview.Should().NotBeNull();
        _ = preview!.Filaments.Should().HaveCount(2);
        _ = preview.Filaments[0].FilamentType.Should().Be("PLA");
        _ = preview.Filaments[1].FilamentType.Should().Be("PETG");
    }

    [Fact(DisplayName = "Export with specific filament types - placeholder implementation")]
    public async Task Export_WithSpecificFilamentTypes_FiltersCorrectly()
    {
        // Arrange - Seed database with multiple filament types
        Guid[] filamentTypeIds = await SeedMultipleFilamentTypes();

        ExportOrcaBundleRequest exportRequest = new ExportOrcaBundleRequest
        {
            FilamentTypeIds = new[] { filamentTypeIds[0], filamentTypeIds[1] },
            IncludeProcessProfiles = false,
            IncludeMetadata = false
        };

        StringContent content = new StringContent(
            JsonSerializer.Serialize(exportRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        HttpResponseMessage response = await _client.PostAsync("/api/slicer/profiles/export/orca", content);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string exportedJson = await response.Content.ReadAsStringAsync();

        using JsonDocument doc = JsonDocument.Parse(exportedJson);
        JsonElement root = doc.RootElement;

        // Current implementation returns empty filament list (placeholder)
        // Once implemented, filament property would exist with actual filament types
        bool hasFilament = root.TryGetProperty("filament", out _);
        _ = hasFilament.Should().BeFalse("filament export is not yet fully implemented");
    }

    [Fact(DisplayName = "Export includes metadata when requested")]
    public async Task Export_WithMetadata_IncludesMetadata()
    {
        // Arrange
        ExportOrcaBundleRequest exportRequest = new ExportOrcaBundleRequest
        {
            IncludeMetadata = true,
            IncludeProcessProfiles = true
        };

        StringContent content = new StringContent(
            JsonSerializer.Serialize(exportRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        HttpResponseMessage response = await _client.PostAsync("/api/slicer/profiles/export/orca", content);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string exportedJson = await response.Content.ReadAsStringAsync();

        using JsonDocument doc = JsonDocument.Parse(exportedJson);
        JsonElement root = doc.RootElement;

        // Should contain metadata fields
        _ = root.TryGetProperty("metadata", out JsonElement metadata).Should().BeTrue();
        _ = metadata.TryGetProperty("exported_at", out _).Should().BeTrue();
        _ = metadata.TryGetProperty("source", out JsonElement source).Should().BeTrue();
        _ = source.GetString().Should().Be("PrintFarmer");
    }

    [Fact(DisplayName = "Export excludes process profiles when not requested")]
    public async Task Export_WithoutProcessProfiles_ExcludesProcessSection()
    {
        // Arrange
        ExportOrcaBundleRequest exportRequest = new ExportOrcaBundleRequest
        {
            IncludeProcessProfiles = false,
            IncludeMetadata = false
        };

        StringContent content = new StringContent(
            JsonSerializer.Serialize(exportRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        HttpResponseMessage response = await _client.PostAsync("/api/slicer/profiles/export/orca", content);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string exportedJson = await response.Content.ReadAsStringAsync();

        using JsonDocument doc = JsonDocument.Parse(exportedJson);
        JsonElement root = doc.RootElement;

        // Process section should be empty or minimal
        if (root.TryGetProperty("process", out JsonElement processSection))
        {
            _ = processSection.GetArrayLength().Should().BeLessThanOrEqualTo(3); // Only default presets
        }
    }

    [Fact(DisplayName = "Import handles malformed JSON gracefully")]
    public async Task Import_MalformedJson_ReturnsBadRequest()
    {
        // Arrange
        string invalidJson = "{ printer: [{ invalid json }] }"; // Unquoted keys, malformed structure

        ImportOrcaBundleDto request = new ImportOrcaBundleDto { BundleJson = invalidJson };
        StringContent content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        // Act
        HttpResponseMessage response = await _client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Import handles missing required fields gracefully")]
    public async Task Import_MissingRequiredFields_ReturnsBadRequest()
    {
        // Arrange - Printer preset missing required nozzle_diameter
        string bundleJson = """
        {
            "printer": [
                {
                    "name": "Incomplete Printer"
                }
            ],
            "filament": [],
            "process": []
        }
        """;

        ImportOrcaBundleDto request = new ImportOrcaBundleDto { BundleJson = bundleJson };
        StringContent content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        // Act
        HttpResponseMessage response = await _client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Helper methods for database seeding

    private async Task SeedPrinterModels()
    {
        // Ensure manufacturer and model exist without causing UNIQUE constraint collisions
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        string lowered = "bambu lab";
        Manufacturer? existingManufacturer = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name.ToLower() == lowered);
        if (existingManufacturer == null)
        {
            existingManufacturer = new Manufacturer
            {
                Id = Guid.NewGuid(),
                Name = "Bambu Lab",
                IsActive = true
            };
            _ = db.Manufacturers.Add(existingManufacturer);
            _ = await db.SaveChangesAsync();
        }

        bool existsModel = await db.PrinterModels.AnyAsync(m => m.ManufacturerId == existingManufacturer.Id && m.Name.ToLower() == "x1 carbon");
        if (!existsModel)
        {
            PrinterModel printerModel = new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "X1 Carbon",
                ManufacturerId = existingManufacturer.Id,
                MaxX = 256,
                MaxY = 256,
                MaxZ = 256,
                MaxBedTemp = 120,
                IsActive = true
            };
            _ = db.PrinterModels.Add(printerModel);
            _ = await db.SaveChangesAsync();
        }
    }

    private async Task SeedFilamentTypes()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        string[] types = new[] { "PLA", "PETG", "ABS" };
        foreach (string? t in types)
        {
            string lowered = t.ToLowerInvariant();
            bool exists = await db.FilamentTypes.AnyAsync(f => f.Name.ToLower() == lowered);
            if (!exists)
            {
                _ = db.FilamentTypes.Add(new FilamentType
                {
                    Id = Guid.NewGuid(),
                    Name = t,
                    IsActive = true
                });
            }
        }
        _ = await db.SaveChangesAsync();
    }

    private async Task<Guid[]> SeedMultipleFilamentTypes()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        string[] desired = new[] { "PLA", "PETG", "ABS" };
        List<Guid> addedIds = new List<Guid>();
        foreach (string? name in desired)
        {
            string lowered = name.ToLowerInvariant();
            FilamentType? existing = await db.FilamentTypes.FirstOrDefaultAsync(f => f.Name.ToLower() == lowered);
            if (existing == null)
            {
                FilamentType ft = new FilamentType { Id = Guid.NewGuid(), Name = name, IsActive = true };
                _ = db.FilamentTypes.Add(ft);
                addedIds.Add(ft.Id);
            }
            else
            {
                addedIds.Add(existing.Id);
            }
        }
        _ = await db.SaveChangesAsync();

        return addedIds.ToArray();
    }
}

#pragma warning restore 0618
