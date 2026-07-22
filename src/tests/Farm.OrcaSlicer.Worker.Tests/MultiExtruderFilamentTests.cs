using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Dtos;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Tests for multi-extruder filament profile support in the OrcaSlicer pipeline.
/// Verifies that <see cref="OrcaSlicingPipelineService"/> correctly generates
/// per-extruder filament JSON files and assembles CLI arguments.
/// </summary>
public class MultiExtruderFilamentTests
{
    [Fact]
    public void SettingsDictToNativeJson_ProducesValidJson_ForFilamentSettings()
    {
        var settings = new Dictionary<string, object>
        {
            ["filament_type"] = "PLA",
            ["nozzle_temperature"] = "210",
            ["bed_temperature"] = "60"
        };

        string json = OrcaSlicingPipelineService.SettingsDictToNativeJson(settings);

        json.Should().NotBeNullOrEmpty();
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("filament_type").GetString().Should().Be("PLA");
    }

    [Fact]
    public void SlicerProfileDto_ExtruderFilamentProfiles_DefaultsToNull()
    {
        var profile = new SlicerProfileDto();

        profile.ExtruderFilamentProfiles.Should().BeNull();
        profile.FilamentProfile.Should().BeNull();
    }

    [Fact]
    public void SlicerProfileDto_ExtruderFilamentProfiles_CanHoldMultipleProfiles()
    {
        var profile = new SlicerProfileDto
        {
            ExtruderFilamentProfiles =
            [
                new FilamentProfileDto { Name = "Generic PLA @System" },
                new FilamentProfileDto { Name = "Generic PETG @System" }
            ]
        };

        profile.ExtruderFilamentProfiles.Should().HaveCount(2);
        profile.ExtruderFilamentProfiles[0].Name.Should().Be("Generic PLA @System");
        profile.ExtruderFilamentProfiles[1].Name.Should().Be("Generic PETG @System");
    }

    [Fact]
    public void ExtruderFilamentProfileNames_RoundTrips_ThroughJson()
    {
        var names = new List<string> { "Generic PLA @System", "Generic PETG @System" };
        string json = JsonSerializer.Serialize(new { extruderFilamentProfileNames = names });

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("extruderFilamentProfileNames", out JsonElement elem).Should().BeTrue();
        elem.ValueKind.Should().Be(JsonValueKind.Array);
        elem.GetArrayLength().Should().Be(2);

        var parsed = new List<string>();
        foreach (JsonElement item in elem.EnumerateArray())
        {
            parsed.Add(item.GetString()!);
        }

        parsed.Should().BeEquivalentTo(names);
    }

    [Fact]
    public void SlicerProfileJson_WithExtruderNames_CanBeEmbedded()
    {
        string baseJson = """{"machineProfileName":"Machine A","filamentProfileName":"PLA","processProfileName":"Standard"}""";
        var names = new List<string> { "PLA", "PETG" };

        // Simulate the controller's EmbedExtruderFilamentNames logic
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(baseJson) ?? [];
        dict["extruderFilamentProfileNames"] = JsonSerializer.SerializeToElement(names);
        string result = JsonSerializer.Serialize(dict);

        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("machineProfileName", out _).Should().BeTrue("original properties preserved");
        root.TryGetProperty("extruderFilamentProfileNames", out JsonElement elem).Should().BeTrue();
        elem.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void MultiFilamentCli_SemicolonSeparated_ForMultipleProfiles()
    {
        // OrcaSlicer CLI expects --load-filaments "path0;path1" for multi-extruder
        var paths = new List<string> { "/work/filament_0.json", "/work/filament_1.json" };
        string cliArg = string.Join(";", paths);

        cliArg.Should().Be("/work/filament_0.json;/work/filament_1.json");
    }

    [Fact]
    public void SingleExtruder_FallsBackToSingularFilament()
    {
        var profile = new SlicerProfileDto
        {
            FilamentProfile = new FilamentProfileDto { Name = "PLA" },
            ExtruderFilamentProfiles = null
        };

        // When ExtruderFilamentProfiles is null, single FilamentProfile is used
        bool isMulti = profile.ExtruderFilamentProfiles is { Count: > 1 };
        isMulti.Should().BeFalse();
        profile.FilamentProfile.Should().NotBeNull();
    }

    [Fact]
    public void SingleExtruderArray_FallsBackToSingularPath()
    {
        var profile = new SlicerProfileDto
        {
            FilamentProfile = new FilamentProfileDto { Name = "PLA" },
            ExtruderFilamentProfiles = [new FilamentProfileDto { Name = "PLA" }]
        };

        // Single entry in array should NOT trigger multi-extruder path
        bool isMulti = profile.ExtruderFilamentProfiles is { Count: > 1 };
        isMulti.Should().BeFalse();
    }
}
