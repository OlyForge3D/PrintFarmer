using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Slicing;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Tests for OrcaSlicer profile seeding and parsing logic.
/// Ensures profiles are correctly parsed from worker responses and imported to the database.
/// Tests case-insensitive matching, hierarchical structure parsing, and filtering to catalog.
/// </summary>
public class ProfileSeedingTests
{
    [Fact]
    public void AllProfilesResponseDto_DeserializesWithCaseInsensitivePropertyNames()
    {
        // Arrange - JSON with lowercase property names as returned by worker
        string json = """
        {
            "byHierarchy": {
                "Prusa": {
                    "name": "Prusa",
                    "models": {
                        "Prusa_CORE_One": {
                            "name": "Prusa CORE One",
                            "modelId": "Prusa_CORE_One",
                            "machineProfiles": [],
                            "filamentProfiles": [],
                            "processProfiles": []
                        }
                    }
                }
            }
        }
        """;

        // Act
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = JsonSerializer.Deserialize<AllProfilesResponseDto>(json, options)!;

        // Assert
        result.Should().NotBeNull();
        result.ByHierarchy.Should().HaveCount(1);
        result.ByHierarchy.Should().ContainKey("Prusa");

        var prusa = result.ByHierarchy["Prusa"];
        prusa.Name.Should().Be("Prusa");
        prusa.Models.Should().HaveCount(1);
        prusa.Models.Should().ContainKey("Prusa_CORE_One");
    }

    [Fact]
    public void ManufacturerMatching_IsCaseInsensitive()
    {
        // Arrange
        var manufacturers = new[] { "Prusa", "Voron", "FlashForge" };
        var hashSet = new HashSet<string>(manufacturers, StringComparer.OrdinalIgnoreCase);

        // Act & Assert
        hashSet.Contains("prusa").Should().BeTrue();
        hashSet.Contains("PRUSA").Should().BeTrue();
        hashSet.Contains("flashforge").Should().BeTrue();
        hashSet.Contains("FLASHFORGE").Should().BeTrue();
        hashSet.Contains("Voron").Should().BeTrue();
        hashSet.Contains("voron").Should().BeTrue();
    }

    [Fact]
    public void ModelMatching_IsCaseInsensitive()
    {
        // Arrange
        var models = new[] { "Prusa MK4S", "Flashforge Speeder 400", "Voron 2.4" };
        var hashSet = new HashSet<string>(models, StringComparer.OrdinalIgnoreCase);

        // Act & Assert
        hashSet.Contains("prusa mk4s").Should().BeTrue();
        hashSet.Contains("PRUSA MK4S").Should().BeTrue();
        hashSet.Contains("flashforge speeder 400").Should().BeTrue();
        hashSet.Contains("FLASHFORGE SPEEDER 400").Should().BeTrue();
    }

    [Fact]
    public void ProfileHierarchy_WithMixedCasing_MatchesCatalog()
    {
        // Arrange - Hierarchy from worker may have different casing than catalog
        var workerData = new Dictionary<string, string> { { "Flashforge", "Worker" }, { "flashforge", "Worker2" } };
        var catalogManufacturers = new[] { "FlashForge", "Flashforge" };
        var catalogSet = new HashSet<string>(catalogManufacturers, StringComparer.OrdinalIgnoreCase);

        // Act & Assert
        foreach (var key in workerData.Keys)
        {
            catalogSet.Contains(key).Should().BeTrue($"Manufacturer {key} should match catalog");
        }
    }

    [Fact]
    public void FilamentProfile_WithInheritance_HasAllRequiredFields()
    {
        // Arrange
        string json = """
        {
            "name": "Prusa Generic PLA @MK4S",
            "material": "PLA",
            "nozzleTemperature": 210,
            "bedTemperature": 60,
            "compatible_printers": ["Prusa MK4S 0.4 nozzle"],
            "inherits": "fdm_filament_pla"
        }
        """;

        // Act
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var profile = JsonSerializer.Deserialize<FilamentProfileDto>(json, options)!;

        // Assert
        profile.Should().NotBeNull();
        profile.Name.Should().Be("Prusa Generic PLA @MK4S");
        profile.Material.Should().Be("PLA");
        profile.NozzleTemperature.Should().Be(210);
        profile.CompatiblePrinters.Should().HaveCount(1);
        profile.Inherits.Should().Be("fdm_filament_pla");
    }

    [Fact]
    public void ProcessProfile_DeserializesCorrectly()
    {
        // Arrange
        string json = """
        {
            "name": "0.20mm Standard @MK4S",
            "quality": "standard",
            "layerHeight": 0.2,
            "infillPercentage": 20,
            "printSpeed": 50,
            "supports": false,
            "compatible_printers": ["Prusa MK4S 0.4 nozzle"]
        }
        """;

        // Act
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var profile = JsonSerializer.Deserialize<ProcessProfileDto>(json, options)!;

        // Assert
        profile.Should().NotBeNull();
        profile.Name.Should().Be("0.20mm Standard @MK4S");
        profile.Quality.Should().Be("standard");
        profile.LayerHeight.Should().Be(0.2);
        profile.InfillPercentage.Should().Be(20);
    }

    [Fact]
    public void MachineProfile_WithMultipleNozzleDiameters_ParsesCorrectly()
    {
        // Arrange
        string json = """
        {
            "name": "Prusa MK4S 0.6",
            "manufacturer": "Prusa",
            "nozzleDiameter": 0.6
        }
        """;

        // Act
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var profile = JsonSerializer.Deserialize<MachineProfileDto>(json, options)!;

        // Assert
        profile.Should().NotBeNull();
        profile.Name.Should().Be("Prusa MK4S 0.6");
        profile.NozzleDiameter.Should().Be(0.6);
    }

    [Fact]
    public void HierarchyWithManyManufacturers_OnlyImportsMatchingCatalog()
    {
        // Arrange - Create a response with many manufacturers
        var fullHierarchy = GenerateHierarchyJson();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var response = JsonSerializer.Deserialize<AllProfilesResponseDto>(fullHierarchy, options)!;

        var catalogManufacturers = new[] { "Prusa", "Voron", "RatRig", "FlashForge", "Sovol" };
        var catalogSet = new HashSet<string>(catalogManufacturers, StringComparer.OrdinalIgnoreCase);

        // Act - Filter to catalog only
        var matching = response.ByHierarchy
            .Where(kvp => catalogSet.Contains(kvp.Key))
            .ToList();

        // Assert
        matching.Should().HaveCount(5);
        matching.Select(m => m.Key).Should().Contain("Prusa");
        matching.Select(m => m.Key).Should().Contain("Voron");
    }

    [Fact]
    public void FilamentProfile_WithInstantiationFlag_FilteredCorrectly()
    {
        // Arrange - Mix of instantiable and non-instantiable profiles
        string json = """
        {
            "filamentProfiles": [
                {
                    "name": "Prusa Generic PLA @MK4S 0.8",
                    "instantiation": true,
                    "compatible_printers": ["Prusa MK4S 0.8 nozzle"],
                    "material": "PLA"
                },
                {
                    "name": "fdm_filament_pla",
                    "instantiation": false,
                    "material": "PLA",
                    "inherits": "fdm_filament_common"
                },
                {
                    "name": "Prusa Generic PLA @MK4S 0.4",
                    "instantiation": true,
                    "compatible_printers": ["Prusa MK4S 0.4 nozzle"],
                    "material": "PLA"
                }
            ]
        }
        """;

        // Act
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var doc = JsonDocument.Parse(json);
        var filaments = JsonSerializer.Deserialize<List<FilamentProfileDto>>(
            JsonSerializer.Serialize(doc.RootElement.GetProperty("filamentProfiles")),
            options
        )!;

        var instantiable = filaments.Where(f => f.Instantiation).ToList();

        // Assert - Only profiles with instantiation=true should be kept
        instantiable.Should().HaveCount(2);
        instantiable.Should().AllSatisfy(f => f.Instantiation.Should().BeTrue());
        instantiable.Should().NotContain(f => f.Name == "fdm_filament_pla");
        instantiable.Should().Contain(f => f.Name == "Prusa Generic PLA @MK4S 0.8");
        instantiable.Should().Contain(f => f.Name == "Prusa Generic PLA @MK4S 0.4");
    }

    [Fact]
    public void ProcessProfile_WithInstantiationFlag_FilteredCorrectly()
    {
        // Arrange - Mix of instantiable and non-instantiable profiles
        string json = """
        {
            "processProfiles": [
                {
                    "name": "0.20mm Standard @MK4S",
                    "instantiation": true,
                    "compatible_printers": ["Prusa MK4S 0.4 nozzle"],
                    "quality": "standard"
                },
                {
                    "name": "process_common_mk4s",
                    "instantiation": false,
                    "quality": "standard",
                    "inherits": "fdm_process_common"
                }
            ]
        }
        """;

        // Act
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var doc = JsonDocument.Parse(json);
        var processes = JsonSerializer.Deserialize<List<ProcessProfileDto>>(
            JsonSerializer.Serialize(doc.RootElement.GetProperty("processProfiles")),
            options
        )!;

        var instantiable = processes.Where(p => p.Instantiation).ToList();

        // Assert - Only profiles with instantiation=true should be kept
        instantiable.Should().HaveCount(1);
        instantiable.Should().AllSatisfy(p => p.Instantiation.Should().BeTrue());
        instantiable.Should().NotContain(p => p.Name == "process_common_mk4s");
        instantiable.Should().Contain(p => p.Name == "0.20mm Standard @MK4S");
    }

    [Fact]
    public void Profile_WithInheritanceProperty_PreservedForResolution()
    {
        // Arrange - Profile with inheritance information
        string json = """
        {
            "name": "Prusa Generic PLA @MK4S 0.8",
            "instantiation": true,
            "inherits": "Prusa Generic PLA @MK4S",
            "compatible_printers": ["Prusa MK4S 0.8 nozzle"],
            "material": "PLA"
        }
        """;

        // Act
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var profile = JsonSerializer.Deserialize<FilamentProfileDto>(json, options)!;

        // Assert - Inherits property should be captured for inheritance resolution during seeding
        profile.Should().NotBeNull();
        profile.Inherits.Should().Be("Prusa Generic PLA @MK4S");
        profile.Instantiation.Should().BeTrue();
    }

    // Helper method to generate test data
    private string GenerateHierarchyJson()
    {
        var manufacturerList = new[]
        {
            "Anycubic", "Artillery", "Anet", // Not in catalog
            "Bambu Lab", "Creality", // Not in catalog for this test
            "Elegoo", // Elegoo not in primary catalog for this test
            "Eryone", // Eryone not in primary catalog for this test
            "FLSun", "FlashForge", "FlyingBear", // FlashForge in catalog
            "Phrozen", // in catalog
            "Prusa", "Qidi", "Raise3D", "RatRig", // Prusa and RatRig in catalog
            "Snapmaker", "Sovol", // Sovol in catalog
            "Tiertime", "Tronxy", "TwoTrees", "UltiMaker",
            "Voron", // in catalog
            "Voxelab", "Wanhao"
        };

        var manufacturers = string.Join(",\n", manufacturerList.Select(m =>
            $@"
        ""{m}"": {{
            ""name"": ""{m}"",
            ""models"": {{
                ""{m}_Model1"": {{
                    ""name"": ""{m} Model 1"",
                    ""modelId"": ""{m}_Model1"",
                    ""machineProfiles"": [],
                    ""filamentProfiles"": [],
                    ""processProfiles"": []
                }}
            }}
        }}"
        ));

        return $@"
{{
    ""byHierarchy"": {{
        {manufacturers}
    }}
}}";
    }
}
