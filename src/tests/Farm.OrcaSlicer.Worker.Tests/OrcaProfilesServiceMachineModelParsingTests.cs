using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Regression coverage for issue #2073: <see cref="OrcaProfilesService"/> was
/// silently returning empty machine-model lists because
/// <c>LoadProfileFromFile&lt;MachineModelProfileDto&gt;</c> had no matching
/// switch case in its type-based dispatcher and so returned <c>null</c> for
/// every <c>machine_model</c> file.
/// </summary>
/// <remarks>
/// The gap surfaced as HTTP 422 <c>source_preset_unavailable</c> from the
/// API's <c>ProfileFamilyRenderer.FindSourceModelMetadata</c> on every
/// clone-family attempt, since <c>MachineModelProfiles</c> in the worker's
/// <c>GET /api/profiles</c> response was always an empty dictionary.
/// </remarks>
public sealed class OrcaProfilesServiceMachineModelParsingTests : IDisposable
{
    private readonly string _profilesRoot;

    public OrcaProfilesServiceMachineModelParsingTests()
    {
        _profilesRoot = Path.Join(
            Path.GetTempPath(),
            "pfarm-orca-machine-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profilesRoot);
    }

    [Fact]
    public async Task ListAvailableMachineModelProfilesAsync_ReturnsFile_WhenInstantiationIsMissing()
    {
        // Real production Prusa machine_model files (e.g. sample_profiles/
        // orcaslicer/Prusa/machine/Prusa CORE One.json) do NOT declare the
        // "instantiation" property at all. Before the fix, LoadProfileFromFile
        // would skip the early-return gate (instantiation absent, so no
        // deferral) and fall through to the switch's "_" arm, returning null.
        WriteManufacturerBundle(
            manufacturer: "Prusa",
            machineModelEntries: [("Prusa CORE One", "machine_model/Prusa CORE One.json")]);
        WriteMachineModelProfile(
            manufacturer: "Prusa",
            subPath: "machine_model/Prusa CORE One.json",
            content: """
            {
              "type": "machine_model",
              "name": "Prusa CORE One",
              "bed_model": "coreone_bed.stl",
              "family": "Prusa",
              "machine_tech": "FFF",
              "model_id": "Prusa_CORE_One",
              "nozzle_diameter": "0.25;0.3;0.4;0.5;0.6;0.8"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        var profiles = await service.ListAvailableMachineModelProfilesAsync();
        var profile = profiles.Should().ContainSingle(
            "the sole machine_model file MUST be parsed — the pre-fix switch " +
            "returned null for MachineModelProfileDto, leaving this list empty").Subject;

        profile.Name.Should().Be("Prusa CORE One");
        profile.Manufacturer.Should().Be("Prusa");
        profile.Settings.Should().ContainKey("model_id", "the Settings dict is " +
            "the only field ProfileFamilyRenderer.BuildMachineModelDocument " +
            "consumes from the machine_model DTO — it must be populated");
        profile.Settings["model_id"].Should().Be("Prusa_CORE_One");
    }

    [Fact]
    public async Task ListAvailableMachineModelProfilesAsync_ReturnsFile_WhenInstantiationIsTrue()
    {
        WriteManufacturerBundle(
            manufacturer: "Bambu",
            machineModelEntries: [("Bambu X1", "machine_model/Bambu X1.json")]);
        WriteMachineModelProfile(
            manufacturer: "Bambu",
            subPath: "machine_model/Bambu X1.json",
            content: """
            {
              "type": "machine_model",
              "name": "Bambu X1",
              "instantiation": "true",
              "family": "Bambu",
              "model_id": "Bambu_X1"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        var profiles = await service.ListAvailableMachineModelProfilesAsync();
        var profile = profiles.Should().ContainSingle().Subject;

        profile.Name.Should().Be("Bambu X1");
    }

    public void Dispose()
    {
        if (Directory.Exists(_profilesRoot))
        {
            Directory.Delete(_profilesRoot, recursive: true);
        }
    }

    private void WriteManufacturerBundle(
        string manufacturer,
        (string name, string subPath)[] machineModelEntries)
    {
        string manufacturerDir = Path.Join(_profilesRoot, manufacturer);
        Directory.CreateDirectory(manufacturerDir);
        Directory.CreateDirectory(Path.Join(manufacturerDir, "machine_model"));

        string machineModelJson = string.Join(
            ",",
            machineModelEntries.Select(entry =>
                $$"""{"name":"{{entry.name}}","sub_path":"{{entry.subPath}}"}"""));

        string bundlePath = Path.Join(_profilesRoot, manufacturer + ".json");
        File.WriteAllText(
            bundlePath,
            $$"""
            {
              "name": "{{manufacturer}}",
              "version": "1.0",
              "machine_model_list": [{{machineModelJson}}],
              "machine_list": [],
              "process_list": [],
              "filament_list": []
            }
            """);
    }

    private void WriteMachineModelProfile(string manufacturer, string subPath, string content)
    {
        string profilePath = Path.Join(
            _profilesRoot,
            manufacturer,
            subPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        File.WriteAllText(profilePath, content);
    }
}
