using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

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
        // wall_loops is loop count, not print speed; print_speed should drive the DTO field.
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

        var profiles = await service.ListAvailableProcessProfilesAsync();
        var profile = profiles.Single();

        profile.PrintSpeed.Should().Be(120);
    }

    [Fact]
    public async Task ListAvailableProcessProfilesAsync_ParsesExplicitFirstLayerValues()
    {
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

        var profiles = await service.ListAvailableProcessProfilesAsync();
        var profile = profiles.Single();

        profile.FirstLayerHeight.Should().BeApproximately(0.28, 0.0001);
        profile.FirstLayerPrintSpeed.Should().Be(35);
    }

    [Fact]
    public async Task ListAvailableProcessProfilesAsync_FallsBackFirstLayerValuesToNormalValues()
    {
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

        var profiles = await service.ListAvailableProcessProfilesAsync();
        var profile = profiles.Single();

        profile.FirstLayerHeight.Should().BeApproximately(0.22, 0.0001);
        profile.FirstLayerPrintSpeed.Should().Be(75);
    }

    [Fact]
    public async Task ListAvailableProcessProfilesAsync_CompatibleConditionMatchesArrayPrinterNotes()
    {
        WriteManufacturerBundle(
            "Prusa",
            machineEntries: [("Prusa CORE One L HF 0.4 nozzle", "machine/core-one-l-hf-04.json")],
            processEntries: [("0.20mm SPEED @CORE One L HF 0.4", "process/core-one-l-hf-speed-04.json")]);
        WriteMachineProfile(
            "Prusa",
            "machine/core-one-l-hf-04.json",
            """
            {
              "name": "Prusa CORE One L HF 0.4 nozzle",
              "instantiation": "true",
              "printer_model": "Prusa CORE One L HF",
              "nozzle_diameter": ["0.4"],
              "printer_notes": ["PRINTER_MODEL_COREONE_L\nHF_NOZZLE\nPG"]
            }
            """);
        WriteProcessProfile(
            "Prusa",
            "process/core-one-l-hf-speed-04.json",
            """
            {
              "name": "0.20mm SPEED @CORE One L HF 0.4",
              "instantiation": "true",
              "layer_height": "0.20",
              "compatible_printers_condition": "printer_notes=~/.*PRINTER_MODEL_COREONE_L[^_a-zA-Z0-9].*/ and nozzle_diameter[0]==0.4 and printer_notes=~/.*HF_NOZZLE.*/"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        var profiles = await service.ListAvailableProcessProfilesAsync();
        var profile = profiles.Single();

        profile.CompatiblePrinters.Should().ContainSingle()
            .Which.Should().Be("Prusa CORE One L HF 0.4 nozzle");
    }

    [Fact]
    public async Task ListAvailableProcessProfilesAsync_CompatibleConditionSupportsNegatedRegex()
    {
        WriteManufacturerBundle(
            "Prusa",
            machineEntries: [("Prusa CORE One L 0.4 nozzle", "machine/core-one-l-04.json")],
            processEntries: [("0.20mm SPEED @CORE One L 0.4", "process/core-one-l-speed-04.json")]);
        WriteMachineProfile(
            "Prusa",
            "machine/core-one-l-04.json",
            """
            {
              "name": "Prusa CORE One L 0.4 nozzle",
              "instantiation": "true",
              "printer_model": "Prusa CORE One L",
              "nozzle_diameter": ["0.4"],
              "printer_notes": "PRINTER_MODEL_COREONE_L\nPG"
            }
            """);
        WriteProcessProfile(
            "Prusa",
            "process/core-one-l-speed-04.json",
            """
            {
              "name": "0.20mm SPEED @CORE One L 0.4",
              "instantiation": "true",
              "layer_height": "0.20",
              "compatible_printers_condition": "printer_notes=~/.*PRINTER_MODEL_COREONE_L[^_a-zA-Z0-9].*/ and nozzle_diameter[0]==0.4 and printer_notes!~/.*HF_NOZZLE.*/"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        var profiles = await service.ListAvailableProcessProfilesAsync();
        var profile = profiles.Single();

        profile.CompatiblePrinters.Should().ContainSingle()
            .Which.Should().Be("Prusa CORE One L 0.4 nozzle");
    }

    [Fact]
    public async Task ListAvailableProcessProfilesAsync_CompatibleConditionWorksWithDeepInheritanceChain()
    {
        // Simulates the real Prusa CORE One L profile chain:
        // process_common_mk4s (non-instantiatable base) →
        //   0.20mm SPEED @MK4S 0.4 (inherits condition for MK4S) →
        //     0.20mm SPEED @CORE One L 0.4 (overrides condition for CORE One L)
        WriteManufacturerBundle(
            "Prusa",
            machineEntries: [("Prusa CORE One L 0.4 nozzle", "machine/Prusa CORE One L 0.4 nozzle.json")],
            processEntries: [("0.20mm SPEED @CORE One L 0.4", "process/0.20mm SPEED @CORE One L 0.4.json")]);
        WriteMachineProfile(
            "Prusa",
            "machine/Prusa CORE One L 0.4 nozzle.json",
            """
            {
              "name": "Prusa CORE One L 0.4 nozzle",
              "instantiation": "true",
              "printer_model": "Prusa CORE One L",
              "nozzle_diameter": ["0.4"],
              "printer_notes": "Don't remove the following keywords!\nPRINTER_MODEL_COREONE_L\nPG\nNO_TEMPLATES"
            }
            """);
        // Base process profile (non-instantiatable)
        WriteProcessProfile(
            "Prusa",
            "process/process_common_mk4s.json",
            """
            {
              "name": "process_common_mk4s",
              "instantiation": "false",
              "print_speed": "200",
              "compatible_printers_condition": "printer_notes=~/.*MK4S.*/"
            }
            """);
        // Mid-level profile (inherits from base, non-instantiatable in practice but marked true for MK4S)
        WriteProcessProfile(
            "Prusa",
            "process/0.20mm SPEED @MK4S 0.4.json",
            """
            {
              "name": "0.20mm SPEED @MK4S 0.4",
              "instantiation": "true",
              "inherits": "process_common_mk4s",
              "layer_height": "0.20",
              "compatible_printers_condition": "printer_notes=~/.*MK4S.*/ and nozzle_diameter[0]==0.4 and printer_notes!~/.*HF_NOZZLE.*/"
            }
            """);
        // Child profile for CORE One L (overrides condition)
        WriteProcessProfile(
            "Prusa",
            "process/0.20mm SPEED @CORE One L 0.4.json",
            """
            {
              "name": "0.20mm SPEED @CORE One L 0.4",
              "instantiation": "true",
              "inherits": "0.20mm SPEED @MK4S 0.4",
              "layer_height": "0.20",
              "compatible_printers_condition": "printer_notes=~/.*PRINTER_MODEL_COREONE_L[^_a-zA-Z0-9].*/ and nozzle_diameter[0]==0.4 and printer_notes!~/.*HF_NOZZLE.*/"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        var profiles = await service.ListAvailableProcessProfilesAsync();

        // Should have 2 instantiatable profiles (MK4S 0.4 and CORE One L 0.4)
        // but only CORE One L 0.4 should match our machine
        var coreOneProfile = profiles.FirstOrDefault(p => p.Name == "0.20mm SPEED @CORE One L 0.4");
        coreOneProfile.Should().NotBeNull("CORE One L process profile should be loaded");
        coreOneProfile!.CompatiblePrinters.Should().Contain("Prusa CORE One L 0.4 nozzle",
            "condition should resolve against the machine's printer_notes");
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
        WriteManufacturerBundle(
            manufacturer,
            machineEntries: [],
            processEntries: [(processName, processSubPath)]);
    }

    private void WriteManufacturerBundle(
        string manufacturer,
        (string name, string subPath)[] machineEntries,
        (string name, string subPath)[] processEntries)
    {
        string manufacturerDir = Path.Combine(_profilesRoot, manufacturer);
        Directory.CreateDirectory(manufacturerDir);
        Directory.CreateDirectory(Path.Combine(manufacturerDir, "machine"));
        Directory.CreateDirectory(Path.Combine(manufacturerDir, "process"));

        string machineJson = FormatBundleEntries(machineEntries);
        string processJson = FormatBundleEntries(processEntries);

        string bundlePath = Path.Combine(_profilesRoot, manufacturer + ".json");
        File.WriteAllText(
            bundlePath,
            $$"""
            {
              "name": "{{manufacturer}}",
              "version": "1.0",
              "description": "test",
              "machine_model_list": [],
              "machine_list": [{{machineJson}}],
              "filament_list": [],
              "process_list": [{{processJson}}]
            }
            """);
    }

    private static string FormatBundleEntries((string name, string subPath)[] entries)
    {
        return string.Join(",", entries.Select(entry =>
            $$"""{"name":"{{entry.name}}","sub_path":"{{entry.subPath}}"}"""));
    }

    private void WriteMachineProfile(string manufacturer, string subPath, string content)
    {
        string profilePath = Path.Combine(_profilesRoot, manufacturer, subPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        File.WriteAllText(profilePath, content);
    }

    private void WriteProcessProfile(string manufacturer, string subPath, string content)
    {
        string profilePath = Path.Combine(_profilesRoot, manufacturer, subPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        File.WriteAllText(profilePath, content);
    }
}
