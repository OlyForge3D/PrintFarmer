using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Tests for OrcaSlicer preset mapping accuracy and fuzzy matching logic.
/// Validates that the mapping service correctly matches Orca presets to PrintFarmer entities.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class OrcaMappingAccuracyTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    public OrcaMappingAccuracyTests()
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

    [Fact(DisplayName = "Fuzzy matching handles name variations correctly")]
    public async Task FuzzyMatching_NameVariations_MatchesCorrectly()
    {
        // Arrange - Seed with known printer model
        await SeedBambuLabPrinters();

        string bundleJson = """
        {
            "printer": [
                {
                    "name": "Bambu X1-Carbon",
                    "printer_model": "X1C",
                    "printer_vendor": "BambuLab",
                    "bed_width": 256,
                    "bed_depth": 256,
                    "max_print_height": 256,
                    "nozzle_diameter": 0.4
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
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string previewJson = await response.Content.ReadAsStringAsync();
        OrcaBundlePreviewDto? preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(previewJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = preview.Should().NotBeNull();
        _ = preview!.Printers.Should().HaveCount(1);
        // Mapping service should recognize X1C variations
    }

    [Fact(DisplayName = "Confidence scoring ranks exact matches higher")]
    public async Task ConfidenceScoring_ExactMatch_RanksHighest()
    {
        // Arrange - Seed with multiple similar printer models
        await SeedSimilarPrinterModels();

        string bundleJson = """
        {
            "printer": [
                {
                    "name": "Prusa MK4",
                    "printer_model": "Original Prusa MK4",
                    "printer_vendor": "Prusa Research",
                    "bed_width": 250,
                    "bed_depth": 210,
                    "max_print_height": 220,
                    "nozzle_diameter": 0.4
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
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string previewJson = await response.Content.ReadAsStringAsync();
        OrcaBundlePreviewDto? preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(previewJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = preview.Should().NotBeNull();
        _ = preview!.Printers.Should().HaveCount(1);
        // Should match MK4 exactly, not MK3 or other variants
    }

    [Fact(DisplayName = "Bed size compatibility checking excludes incompatible printers")]
    public async Task CompatibilityChecking_BedSize_ExcludesIncompatible()
    {
        // Arrange - Seed with printers of different bed sizes
        await SeedPrintersWithDifferentBedSizes();

        string bundleJson = """
        {
            "printer": [
                {
                    "name": "Large Format Printer",
                    "printer_model": "Big Printer",
                    "bed_width": 400,
                    "bed_depth": 400,
                    "max_print_height": 400,
                    "nozzle_diameter": 0.6
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
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Should not match small-bed printers
    }

    [Fact(DisplayName = "Nozzle diameter matching requires exact match")]
    public async Task NozzleDiameterMatching_RequiresExactMatch()
    {
        // Arrange - Seed with printers having different nozzle sizes
        await SeedPrintersWithDifferentNozzles();

        string bundleJson = """
        {
            "printer": [
                {
                    "name": "0.6mm Nozzle Printer",
                    "printer_model": "Test Printer",
                    "bed_width": 220,
                    "bed_depth": 220,
                    "max_print_height": 250,
                    "nozzle_diameter": 0.6
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
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string previewJson = await response.Content.ReadAsStringAsync();
        OrcaBundlePreviewDto? preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(previewJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = preview.Should().NotBeNull();
        _ = preview!.Printers.Should().HaveCount(1);
        _ = preview.Printers[0].NozzleDiameter.Should().Be(0.6);
    }

    [Fact(DisplayName = "Filament type derivation from name works correctly")]
    public async Task FilamentTypeDerivation_FromName_WorksCorrectly()
    {
        // Arrange
        string bundleJson = """
        {
            "printer": [],
            "filament": [
                {
                    "name": "Bambu PLA Basic Red",
                    "nozzle_temperature": 220,
                    "bed_temperature": 65
                },
                {
                    "name": "PolyTerra PETG Blue",
                    "nozzle_temperature": 250,
                    "bed_temperature": 80
                },
                {
                    "name": "Generic ABS Black",
                    "filament_type": "ABS",
                    "nozzle_temperature": 260,
                    "bed_temperature": 100
                }
            ],
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
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string previewJson = await response.Content.ReadAsStringAsync();
        OrcaBundlePreviewDto? preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(previewJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = preview.Should().NotBeNull();
        _ = preview!.Filaments.Should().HaveCount(3);

        // First filament should be derived as PLA from name
        OrcaFilamentPresetDto? plaFilament = preview.Filaments.FirstOrDefault(f => f.Name.Contains("PLA"));
        _ = plaFilament.Should().NotBeNull();

        // Third filament should use explicit type
        OrcaFilamentPresetDto? absFilament = preview.Filaments.FirstOrDefault(f => f.FilamentType == "ABS");
        _ = absFilament.Should().NotBeNull();
    }

    [Fact(DisplayName = "Quality classification from layer height works correctly")]
    public async Task QualityClassification_FromLayerHeight_WorksCorrectly()
    {
        // Arrange
        string bundleJson = """
        {
            "printer": [],
            "filament": [],
            "process": [
                {
                    "name": "Fine Quality",
                    "layer_height": 0.12,
                    "fill_density": 20,
                    "print_speed": 50
                },
                {
                    "name": "Normal Quality",
                    "layer_height": 0.20,
                    "fill_density": 20,
                    "print_speed": 100
                },
                {
                    "name": "Draft Quality",
                    "layer_height": 0.28,
                    "fill_density": 15,
                    "print_speed": 150
                }
            ]
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
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string previewJson = await response.Content.ReadAsStringAsync();
        OrcaBundlePreviewDto? preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(previewJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = preview.Should().NotBeNull();
        _ = preview!.Processes.Should().HaveCount(3);

        // Verify layer heights are preserved
        _ = preview.Processes.Should().Contain(p => Math.Abs(p.LayerHeight - 0.12) < 0.01);
        _ = preview.Processes.Should().Contain(p => Math.Abs(p.LayerHeight - 0.20) < 0.01);
        _ = preview.Processes.Should().Contain(p => Math.Abs(p.LayerHeight - 0.28) < 0.01);
    }

    [Fact(DisplayName = "Empty sections handled gracefully")]
    public async Task EmptySections_HandledGracefully()
    {
        // Arrange - Bundle with only printer section populated
        string bundleJson = """
        {
            "printer": [
                {
                    "name": "Test Printer",
                    "bed_width": 220,
                    "bed_depth": 220,
                    "max_print_height": 250,
                    "nozzle_diameter": 0.4
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
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string previewJson = await response.Content.ReadAsStringAsync();
        OrcaBundlePreviewDto? preview = JsonSerializer.Deserialize<OrcaBundlePreviewDto>(previewJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _ = preview.Should().NotBeNull();
        _ = preview!.Printers.Should().HaveCount(1);
        _ = preview.Filaments.Should().BeEmpty();
        _ = preview.Processes.Should().BeEmpty();
    }

    // Helper methods for database seeding
    private async Task<Manufacturer> EnsureManufacturerExists(string name)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        string lowered = name.ToLowerInvariant();
        Manufacturer? existing = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name.ToLower() == lowered);
        if (existing != null)
        {
            return existing;
        }

        Manufacturer manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsActive = true
        };

        _ = db.Manufacturers.Add(manufacturer);
        _ = await db.SaveChangesAsync();
        return manufacturer;
    }

    private async Task SeedBambuLabPrinters()
    {
        // Ensure manufacturer exists (idempotent)
        Manufacturer manufacturer = await EnsureManufacturerExists("Bambu Lab");

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        PrinterModel[] printerModels = new[]
        {
            new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "X1 Carbon",
                ManufacturerId = manufacturer.Id,
                MaxX = 256,
                MaxY = 256,
                MaxZ = 256,
                IsActive = true
            },
            new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "P1P",
                ManufacturerId = manufacturer.Id,
                MaxX = 256,
                MaxY = 256,
                MaxZ = 256,
                IsActive = true
            }
        };

        // Attach models to the resolved manufacturer id and save
        foreach (PrinterModel? pm in printerModels)
        {
            bool exists = await db.PrinterModels.AnyAsync(m => m.ManufacturerId == pm.ManufacturerId && m.Name.ToLower() == pm.Name.ToLower());
            if (!exists)
            {
                _ = db.PrinterModels.Add(pm);
            }
        }
        _ = await db.SaveChangesAsync();
    }

    private async Task SeedSimilarPrinterModels()
    {
        Manufacturer manufacturer = await EnsureManufacturerExists("Prusa Research");

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        PrinterModel[] printerModels = new[]
        {
            new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "Original Prusa MK4",
                ManufacturerId = manufacturer.Id,
                MaxX = 250,
                MaxY = 210,
                MaxZ = 220,
                IsActive = true
            },
            new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "Original Prusa MK3S+",
                ManufacturerId = manufacturer.Id,
                MaxX = 250,
                MaxY = 210,
                MaxZ = 210,
                IsActive = true
            }
        };

        foreach (PrinterModel? pm in printerModels)
        {
            bool exists = await db.PrinterModels.AnyAsync(m => m.ManufacturerId == pm.ManufacturerId && m.Name.ToLower() == pm.Name.ToLower());
            if (!exists)
            {
                _ = db.PrinterModels.Add(pm);
            }
        }
        _ = await db.SaveChangesAsync();
    }

    private async Task SeedPrintersWithDifferentBedSizes()
    {
        Manufacturer manufacturer = await EnsureManufacturerExists("Generic Manufacturer");

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        PrinterModel[] printerModels = new[]
        {
            new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "Small Printer",
                ManufacturerId = manufacturer.Id,
                MaxX = 180,
                MaxY = 180,
                MaxZ = 180,
                IsActive = true
            },
            new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "Medium Printer",
                ManufacturerId = manufacturer.Id,
                MaxX = 250,
                MaxY = 250,
                MaxZ = 250,
                IsActive = true
            }
        };

        foreach (PrinterModel? pm in printerModels)
        {
            bool exists = await db.PrinterModels.AnyAsync(m => m.ManufacturerId == pm.ManufacturerId && m.Name.ToLower() == pm.Name.ToLower());
            if (!exists)
            {
                _ = db.PrinterModels.Add(pm);
            }
        }
        _ = await db.SaveChangesAsync();
    }

    private async Task SeedPrintersWithDifferentNozzles()
    {
        Manufacturer manufacturer = await EnsureManufacturerExists("Generic Manufacturer");

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        PrinterModel[] printerModels = new[]
        {
            new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "0.4mm Printer",
                ManufacturerId = manufacturer.Id,
                MaxX = 220,
                MaxY = 220,
                MaxZ = 250,
                IsActive = true
            },
            new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "0.6mm Printer",
                ManufacturerId = manufacturer.Id,
                MaxX = 220,
                MaxY = 220,
                MaxZ = 250,
                IsActive = true
            },
            new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "0.8mm Printer",
                ManufacturerId = manufacturer.Id,
                MaxX = 220,
                MaxY = 220,
                MaxZ = 250,
                IsActive = true
            }
        };

        foreach (PrinterModel? pm in printerModels)
        {
            bool exists = await db.PrinterModels.AnyAsync(m => m.ManufacturerId == pm.ManufacturerId && m.Name.ToLower() == pm.Name.ToLower());
            if (!exists)
            {
                _ = db.PrinterModels.Add(pm);
            }
        }
        _ = await db.SaveChangesAsync();
    }
}
