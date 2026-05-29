using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Slicer.Module.Tests.SlicerServices;

public sealed class OrcaProfilesServiceProcessParsingTests : IDisposable
{
    private readonly string _profilesRoot;

    public OrcaProfilesServiceProcessParsingTests()
    {
        _profilesRoot = Path.Combine(Path.GetTempPath(), "pfarm-orca-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profilesRoot);
    }

    [Fact]
    public async Task ListAvailableProcessProfilesAsync_UsesPrintSpeed_NotWallLoops()
    {
        // Arrange
        WriteManufacturerBundle("Acme", "Speed Profile", "process/speed.json");
        WriteProcessProfile(
            "Acme",
            "process/speed.json",
            """
            {
              "name": "Speed Profile",
              "instantiation": "true",
              "layer_height": "0.20",
              "fill_density": "20",
              "wall_loops": "3",
              "print_speed": "120",
              "enable_support": "0"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        // Act
        var profiles = await service.ListAvailableProcessProfilesAsync();
        var profile = profiles.Single();

        // Assert
        profile.PrintSpeed.Should().Be(120);
    }

    [Fact]
    public async Task ListAvailableProcessProfilesAsync_ParsesExplicitFirstLayerValues()
    {
        // Arrange
        WriteManufacturerBundle("Acme", "First Layer Profile", "process/first-layer.json");
        WriteProcessProfile(
            "Acme",
            "process/first-layer.json",
            """
            {
              "name": "First Layer Profile",
              "instantiation": "true",
              "layer_height": "0.20",
              "fill_density": "15",
              "print_speed": "90",
              "initial_layer_print_height": "0.28",
              "initial_layer_speed": ["35"]
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        // Act
        var profiles = await service.ListAvailableProcessProfilesAsync();
        var profile = profiles.Single();

        // Assert
        profile.FirstLayerHeight.Should().BeApproximately(0.28, 0.0001);
        profile.FirstLayerPrintSpeed.Should().Be(35);
    }

    [Fact]
    public async Task ListAvailableProcessProfilesAsync_FallsBackFirstLayerValuesToNormalValues()
    {
        // Arrange
        WriteManufacturerBundle("Acme", "Fallback Profile", "process/fallback.json");
        WriteProcessProfile(
            "Acme",
            "process/fallback.json",
            """
            {
              "name": "Fallback Profile",
              "instantiation": "true",
              "layer_height": "0.22",
              "fill_density": "18",
              "print_speed": "75"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        // Act
        var profiles = await service.ListAvailableProcessProfilesAsync();
        var profile = profiles.Single();

        // Assert
        profile.FirstLayerHeight.Should().BeApproximately(0.22, 0.0001);
        profile.FirstLayerPrintSpeed.Should().Be(75);
    }

    public void Dispose()
    {
        if (Directory.Exists(_profilesRoot))
        {
            Directory.Delete(_profilesRoot, recursive: true);
        }
    }

    private void WriteManufacturerBundle(string manufacturer, string processName, string processSubPath)
    {
        string manufacturerDir = Path.Combine(_profilesRoot, manufacturer);
        Directory.CreateDirectory(manufacturerDir);
        Directory.CreateDirectory(Path.Combine(manufacturerDir, "process"));

        string bundlePath = Path.Combine(_profilesRoot, manufacturer + ".json");
        File.WriteAllText(
            bundlePath,
            $$"""
            {
              "name": "{{manufacturer}}",
              "version": "1.0",
              "description": "test",
              "machine_model_list": [],
              "machine_list": [],
              "filament_list": [],
              "process_list": [
                {
                  "name": "{{processName}}",
                  "sub_path": "{{processSubPath}}"
                }
              ]
            }
            """);
    }

    private void WriteProcessProfile(string manufacturer, string subPath, string content)
    {
        string profilePath = Path.Combine(_profilesRoot, manufacturer, subPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        File.WriteAllText(profilePath, content);
    }
}
