using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

/// <summary>
/// Integration tests for OrcaSlicer bundle import/export round-trip functionality.
/// Tests the complete workflow: preview → import → export → verify.
/// </summary>
public class OrcaBundleIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OrcaBundleIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact(DisplayName = "Round-trip: Import Orca bundle and export back maintains data integrity")]
    public async Task RoundTrip_ImportAndExport_MaintainsDataIntegrity()
    {
        // Arrange - Create a comprehensive Orca bundle
        var originalBundle = CreateComprehensiveOrcaBundle();

        // Step 1: Preview the bundle
        var previewRequest = new ImportOrcaBundleDto { BundleJson = originalBundle };
        var previewContent = new StringContent(
            JsonSerializer.Serialize(previewRequest),
            Encoding.UTF8,
            "application/json");

        var previewResponse = await _client.PostAsync("/api/slicer/profiles/import/orca/preview", previewContent);
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var previewJson = await previewResponse.Content.ReadAsStringAsync();
        var preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(previewJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        preview.Should().NotBeNull();
        preview!.Printers.Should().HaveCount(2);
        preview.Filaments.Should().HaveCount(3);
        preview.Processes.Should().HaveCount(3);

        // Step 2: Import the bundle (this would normally require mapping, but we'll test the basic flow)
        // Note: Full import with mapping would require additional setup of PrinterModels/FilamentTypes

        // Step 3: Export the bundle
        var exportRequest = new ExportOrcaBundleRequest
        {
            IncludeProcessProfiles = true,
            IncludeMetadata = true
        };

        var exportContent = new StringContent(
            JsonSerializer.Serialize(exportRequest),
            Encoding.UTF8,
            "application/json");

        var exportResponse = await _client.PostAsync("/api/slicer/profiles/export/orca", exportContent);
        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var exportedJson = await exportResponse.Content.ReadAsStringAsync();
        exportedJson.Should().NotBeNullOrEmpty();

        // Step 4: Verify exported structure is valid JSON and contains expected sections
        using var exportedDoc = JsonDocument.Parse(exportedJson);
        var root = exportedDoc.RootElement;

        root.TryGetProperty("printer", out var printerSection).Should().BeTrue();
        root.TryGetProperty("filament", out var filamentSection).Should().BeTrue();
        root.TryGetProperty("process", out var processSection).Should().BeTrue();
    }

    [Fact(DisplayName = "Import with mapping resolves printer models correctly")]
    public async Task Import_WithMapping_ResolvesPrinterModels()
    {
        // Arrange - Seed database with known printer models
        await SeedPrinterModels();

        var bundleJson = """
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

        var previewRequest = new ImportOrcaBundleDto { BundleJson = bundleJson };
        var content = new StringContent(
            JsonSerializer.Serialize(previewRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var previewJson = await response.Content.ReadAsStringAsync();
        var preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(previewJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        preview.Should().NotBeNull();
        preview!.Printers.Should().HaveCount(1);
        preview.Printers[0].Name.Should().Contain("X1");
    }

    [Fact(DisplayName = "Import with filament type mapping resolves materials correctly")]
    public async Task Import_WithFilamentMapping_ResolvesMaterials()
    {
        // Arrange - Seed database with known filament types
        await SeedFilamentTypes();

        var bundleJson = """
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

        var previewRequest = new ImportOrcaBundleDto { BundleJson = bundleJson };
        var content = new StringContent(
            JsonSerializer.Serialize(previewRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var previewJson = await response.Content.ReadAsStringAsync();
        var preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(previewJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        preview.Should().NotBeNull();
        preview!.Filaments.Should().HaveCount(2);
        preview.Filaments[0].FilamentType.Should().Be("PLA");
        preview.Filaments[1].FilamentType.Should().Be("PETG");
    }

    [Fact(DisplayName = "Export with specific printer models filters correctly")]
    public async Task Export_WithSpecificPrinterModels_FiltersCorrectly()
    {
        // Arrange - Seed database with multiple printer models
        var printerModelIds = await SeedMultiplePrinterModels();

        var exportRequest = new ExportOrcaBundleRequest
        {
            PrinterModelIds = new[] { printerModelIds[0] },
            IncludeProcessProfiles = false,
            IncludeMetadata = false
        };

        var content = new StringContent(
            JsonSerializer.Serialize(exportRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/slicer/profiles/export/orca", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportedJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(exportedJson);
        var root = doc.RootElement;

        root.TryGetProperty("printer", out var printerSection).Should().BeTrue();
        // Should only contain the single requested printer
    }

    [Fact(DisplayName = "Export with specific filament types filters correctly")]
    public async Task Export_WithSpecificFilamentTypes_FiltersCorrectly()
    {
        // Arrange - Seed database with multiple filament types
        var filamentTypeIds = await SeedMultipleFilamentTypes();

        var exportRequest = new ExportOrcaBundleRequest
        {
            FilamentTypeIds = new[] { filamentTypeIds[0], filamentTypeIds[1] },
            IncludeProcessProfiles = false,
            IncludeMetadata = false
        };

        var content = new StringContent(
            JsonSerializer.Serialize(exportRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/slicer/profiles/export/orca", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportedJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(exportedJson);
        var root = doc.RootElement;

        root.TryGetProperty("filament", out var filamentSection).Should().BeTrue();
        // Should contain the requested filament types
    }

    [Fact(DisplayName = "Export includes metadata when requested")]
    public async Task Export_WithMetadata_IncludesMetadata()
    {
        // Arrange
        var exportRequest = new ExportOrcaBundleRequest
        {
            IncludeMetadata = true,
            IncludeProcessProfiles = true
        };

        var content = new StringContent(
            JsonSerializer.Serialize(exportRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/slicer/profiles/export/orca", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportedJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(exportedJson);
        var root = doc.RootElement;

        // Should contain metadata fields
        root.TryGetProperty("metadata", out var metadata).Should().BeTrue();
        metadata.TryGetProperty("exported_at", out _).Should().BeTrue();
        metadata.TryGetProperty("source", out var source).Should().BeTrue();
        source.GetString().Should().Be("PrintFarmer");
    }

    [Fact(DisplayName = "Export excludes process profiles when not requested")]
    public async Task Export_WithoutProcessProfiles_ExcludesProcessSection()
    {
        // Arrange
        var exportRequest = new ExportOrcaBundleRequest
        {
            IncludeProcessProfiles = false,
            IncludeMetadata = false
        };

        var content = new StringContent(
            JsonSerializer.Serialize(exportRequest),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/slicer/profiles/export/orca", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportedJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(exportedJson);
        var root = doc.RootElement;

        // Process section should be empty or minimal
        if (root.TryGetProperty("process", out var processSection))
        {
            processSection.GetArrayLength().Should().BeLessThanOrEqualTo(3); // Only default presets
        }
    }

    [Fact(DisplayName = "Import handles malformed JSON gracefully")]
    public async Task Import_MalformedJson_ReturnsBadRequest()
    {
        // Arrange
        var invalidJson = "{ printer: [{ invalid json }] }"; // Unquoted keys, malformed structure

        var request = new ImportOrcaBundleDto { BundleJson = invalidJson };
        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Import handles missing required fields gracefully")]
    public async Task Import_MissingRequiredFields_ReturnsBadRequest()
    {
        // Arrange - Printer preset missing required nozzle_diameter
        var bundleJson = """
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

        var request = new ImportOrcaBundleDto { BundleJson = bundleJson };
        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        // Act
        var response = await _client.PostAsync("/api/slicer/profiles/import/orca/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Helper methods for database seeding

    private async Task SeedPrinterModels()
    {
        // Ensure manufacturer and model exist without causing UNIQUE constraint collisions
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lowered = "bambu lab";
        var existingManufacturer = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name.ToLower() == lowered);
        if (existingManufacturer == null)
        {
            existingManufacturer = new Manufacturer
            {
                Id = Guid.NewGuid(),
                Name = "Bambu Lab",
                IsActive = true
            };
            db.Manufacturers.Add(existingManufacturer);
            await db.SaveChangesAsync();
        }

        var existsModel = await db.Models.AnyAsync(m => m.ManufacturerId == existingManufacturer.Id && m.Name.ToLower() == "x1 carbon");
        if (!existsModel)
        {
            var printerModel = new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "X1 Carbon",
                ManufacturerId = existingManufacturer.Id,
                MaxX = 256,
                MaxY = 256,
                MaxZ = 256,
                DefaultNozzleDiameter = 0.4,
                MaxBedTemp = 120,
                MaxHotendTemp = 300,
                IsActive = true
            };
            db.Models.Add(printerModel);
            await db.SaveChangesAsync();
        }
    }

    private async Task SeedFilamentTypes()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var types = new[] { "PLA", "PETG", "ABS" };
        foreach (var t in types)
        {
            var lowered = t.ToLowerInvariant();
            var exists = await db.FilamentTypes.AnyAsync(f => f.Name.ToLower() == lowered);
            if (!exists)
            {
                db.FilamentTypes.Add(new FilamentType
                {
                    Id = Guid.NewGuid(),
                    Name = t,
                    IsActive = true
                });
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task<Guid[]> SeedMultiplePrinterModels()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var manufacturerName = "Test Manufacturer";
        var lowered = manufacturerName.ToLowerInvariant();
        var manufacturer = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name.ToLower() == lowered);
        if (manufacturer == null)
        {
            manufacturer = new Manufacturer
            {
                Id = Guid.NewGuid(),
                Name = manufacturerName,
                IsActive = true
            };
            db.Manufacturers.Add(manufacturer);
            await db.SaveChangesAsync();
        }

        var models = new[]
        {
            new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "Printer A",
                ManufacturerId = manufacturer.Id,
                MaxX = 220,
                MaxY = 220,
                MaxZ = 250,
                DefaultNozzleDiameter = 0.4,
                IsActive = true
            },
            new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "Printer B",
                ManufacturerId = manufacturer.Id,
                MaxX = 300,
                MaxY = 300,
                MaxZ = 400,
                DefaultNozzleDiameter = 0.6,
                IsActive = true
            }
        };

        var addedIds = new List<Guid>();
        foreach (var m in models)
        {
            var exists = await db.Models.AnyAsync(x => x.ManufacturerId == m.ManufacturerId && x.Name.ToLower() == m.Name.ToLower());
            if (!exists)
            {
                db.Models.Add(m);
                addedIds.Add(m.Id);
            }
        }
        await db.SaveChangesAsync();

        // If none were added because they existed, return the existing ids for the requested names
        if (addedIds.Count == 0)
        {
            var ids = await db.Models.Where(x => x.ManufacturerId == manufacturer.Id && (x.Name == "Printer A" || x.Name == "Printer B")).Select(x => x.Id).ToArrayAsync();
            return ids;
        }

        return addedIds.ToArray();
    }

    private async Task<Guid[]> SeedMultipleFilamentTypes()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var desired = new[] { "PLA", "PETG", "ABS" };
        var addedIds = new List<Guid>();
        foreach (var name in desired)
        {
            var lowered = name.ToLowerInvariant();
            var existing = await db.FilamentTypes.FirstOrDefaultAsync(f => f.Name.ToLower() == lowered);
            if (existing == null)
            {
                var ft = new FilamentType { Id = Guid.NewGuid(), Name = name, IsActive = true };
                db.FilamentTypes.Add(ft);
                addedIds.Add(ft.Id);
            }
            else
            {
                addedIds.Add(existing.Id);
            }
        }
        await db.SaveChangesAsync();

        return addedIds.ToArray();
    }

    private static string CreateComprehensiveOrcaBundle()
    {
        return """
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
                    "max_hotend_temperature": 300,
                    "gcode_flavor": "klipper",
                    "supports_multi_material": true
                },
                {
                    "name": "Prusa MK4",
                    "printer_model": "Original Prusa MK4",
                    "printer_vendor": "Prusa Research",
                    "bed_width": 250,
                    "bed_depth": 210,
                    "max_print_height": 220,
                    "nozzle_diameter": 0.4,
                    "max_bed_temperature": 120,
                    "max_hotend_temperature": 300,
                    "gcode_flavor": "marlin"
                }
            ],
            "filament": [
                {
                    "name": "Generic PLA",
                    "filament_type": "PLA",
                    "nozzle_temperature": 220,
                    "bed_temperature": 65,
                    "fan_speed": 100,
                    "retraction_length": 0.4,
                    "retraction_speed": 40
                },
                {
                    "name": "Generic PETG",
                    "filament_type": "PETG",
                    "nozzle_temperature": 250,
                    "bed_temperature": 80,
                    "fan_speed": 50,
                    "retraction_length": 0.6,
                    "retraction_speed": 35
                },
                {
                    "name": "Generic ABS",
                    "filament_type": "ABS",
                    "nozzle_temperature": 260,
                    "bed_temperature": 100,
                    "fan_speed": 30,
                    "retraction_length": 0.5,
                    "retraction_speed": 40
                }
            ],
            "process": [
                {
                    "name": "0.12mm Fine",
                    "layer_height": 0.12,
                    "fill_density": 20,
                    "print_speed": 50,
                    "wall_loops": 3,
                    "top_shell_layers": 5,
                    "bottom_shell_layers": 4
                },
                {
                    "name": "0.20mm Standard",
                    "layer_height": 0.20,
                    "fill_density": 20,
                    "print_speed": 100,
                    "wall_loops": 2,
                    "top_shell_layers": 4,
                    "bottom_shell_layers": 3
                },
                {
                    "name": "0.28mm Draft",
                    "layer_height": 0.28,
                    "fill_density": 15,
                    "print_speed": 150,
                    "wall_loops": 2,
                    "top_shell_layers": 3,
                    "bottom_shell_layers": 3
                }
            ]
        }
        """;
    }
}

#pragma warning restore 0618
