using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Tests for profile inheritance resolution in OrcaProfilesService.
/// Exercises BuildResolvedProfileJson → CollectInheritanceChainAsJson → MergeProfilesJson → FindParentProfile
/// through the public ListAvailable*ProfilesAsync methods.
/// </summary>
public sealed class OrcaProfilesServiceInheritanceTests : IDisposable
{
    private readonly string _profilesRoot;

    public OrcaProfilesServiceInheritanceTests()
    {
        _profilesRoot = Path.Combine(Path.GetTempPath(), "pfarm-orca-inheritance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profilesRoot);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 1. Simple inheritance: child inherits from parent
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SimpleInheritance_ChildGetsParentSettings()
    {
        // Parent has retraction_length, nozzle_diameter, printable_area
        // Child inherits parent and overrides retraction_length
        WriteManufacturerBundle("TestMfg", machineEntries: [
            ("Child Machine 0.4 nozzle", "machine/child.json"),
        ]);

        WriteProfile("TestMfg", "machine/base.json", """
            {
              "name": "Base Machine",
              "instantiation": "false",
              "nozzle_diameter": ["0.4"],
              "printable_area": "0x0,220x0,220x220,0x220",
              "printable_height": "250",
              "retraction_length": ["0.8"],
              "machine_max_speed_x": ["300"],
              "machine_max_speed_y": ["300"],
              "gcode_flavor": "marlin"
            }
            """);

        WriteProfile("TestMfg", "machine/child.json", """
            {
              "name": "Child Machine 0.4 nozzle",
              "inherits": "base",
              "instantiation": "true",
              "retraction_length": ["1.2"]
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        profiles.Should().HaveCount(1);
        var profile = profiles[0];

        // From parent
        profile.NozzleDiameter.Should().BeApproximately(0.4, 0.001);
        profile.MaxFeedrateX.Should().Be(300);
        profile.GcodeDialect.Should().Be("marlin");

        // Overridden by child
        profile.RetractionLength.Should().Be(1.2);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. Multi-level inheritance: C → B → A
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MultiLevelInheritance_ResolvesFullChain()
    {
        WriteManufacturerBundle("TestMfg", machineEntries: [
            ("Grandchild 0.4 nozzle", "machine/grandchild.json"),
        ]);

        // A: base with core settings
        WriteProfile("TestMfg", "machine/level_a.json", """
            {
              "name": "Level A Base",
              "instantiation": "false",
              "nozzle_diameter": ["0.4"],
              "printable_area": "0x0,300x0,300x300,0x300",
              "printable_height": "340",
              "gcode_flavor": "klipper",
              "machine_max_speed_x": ["500"],
              "machine_max_speed_y": ["500"],
              "retraction_length": ["0.5"]
            }
            """);

        // B: inherits A, overrides max speed
        WriteProfile("TestMfg", "machine/level_b.json", """
            {
              "name": "Level B Mid",
              "inherits": "level_a",
              "instantiation": "false",
              "machine_max_speed_x": ["300"],
              "machine_max_speed_y": ["300"],
              "retraction_length": ["0.8"]
            }
            """);

        // C: inherits B, overrides retraction
        WriteProfile("TestMfg", "machine/grandchild.json", """
            {
              "name": "Grandchild 0.4 nozzle",
              "inherits": "level_b",
              "instantiation": "true",
              "retraction_length": ["1.5"]
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        profiles.Should().HaveCount(1);
        var profile = profiles[0];

        // From A (not overridden)
        profile.NozzleDiameter.Should().BeApproximately(0.4, 0.001);
        profile.GcodeDialect.Should().Be("klipper");

        // From B (overrode A)
        profile.MaxFeedrateX.Should().Be(300);

        // From C (overrode B)
        profile.RetractionLength.Should().Be(1.5);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. Array value handling: parent arrays propagate to child
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ArrayValues_PropagateFromParent()
    {
        WriteManufacturerBundle("TestMfg", machineEntries: [
            ("Array Child 0.6 nozzle", "machine/array_child.json"),
        ]);

        WriteProfile("TestMfg", "machine/array_base.json", """
            {
              "name": "Array Base",
              "instantiation": "false",
              "nozzle_diameter": ["0.6"],
              "printable_area": "0x0,220x0,220x220,0x220",
              "printable_height": "250",
              "machine_max_speed_x": ["300", "300"],
              "machine_max_speed_y": ["250", "250"],
              "retraction_length": ["0.8"]
            }
            """);

        WriteProfile("TestMfg", "machine/array_child.json", """
            {
              "name": "Array Child 0.6 nozzle",
              "inherits": "array_base",
              "instantiation": "true"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        profiles.Should().HaveCount(1);
        var profile = profiles[0];

        // Parent array values should survive in the settings dict
        profile.Settings.Should().ContainKey("machine_max_speed_x");

        // Verify array structure is preserved
        string rawValue = profile.Settings["machine_max_speed_x"].ToString()!;
        using var doc = JsonDocument.Parse(rawValue);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(2);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. Child overrides parent value
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChildOverridesParent_UsesChildValue()
    {
        WriteManufacturerBundle("TestMfg", machineEntries: [
            ("Override Child 0.4 nozzle", "machine/override_child.json"),
        ]);

        WriteProfile("TestMfg", "machine/override_base.json", """
            {
              "name": "Override Base",
              "instantiation": "false",
              "nozzle_diameter": ["0.4"],
              "printable_area": "0x0,220x0,220x220,0x220",
              "printable_height": "250",
              "retraction_length": ["0.8"],
              "retraction_speed": ["40"],
              "machine_max_speed_x": ["200"]
            }
            """);

        WriteProfile("TestMfg", "machine/override_child.json", """
            {
              "name": "Override Child 0.4 nozzle",
              "inherits": "override_base",
              "instantiation": "true",
              "retraction_length": ["1.2"],
              "retraction_speed": ["60"],
              "machine_max_speed_x": ["350"]
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        profiles.Should().HaveCount(1);
        var profile = profiles[0];

        profile.RetractionLength.Should().Be(1.2);
        profile.RetractionSpeed.Should().Be(60);
        profile.MaxFeedrateX.Should().Be(350);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 5. Abstract profile (instantiation: false) is not returned as visible
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AbstractProfile_NotReturnedAsVisible_ButUsableAsParent()
    {
        // Bundle lists both profiles, but only the child has instantiation=true
        WriteManufacturerBundle("TestMfg", machineEntries: [
            ("Abstract Base", "machine/abstract_base.json"),
            ("Concrete Child 0.4 nozzle", "machine/concrete_child.json"),
        ]);

        WriteProfile("TestMfg", "machine/abstract_base.json", """
            {
              "name": "Abstract Base",
              "instantiation": "false",
              "nozzle_diameter": ["0.4"],
              "printable_area": "0x0,220x0,220x220,0x220",
              "printable_height": "250",
              "gcode_flavor": "marlin",
              "retraction_length": ["0.8"]
            }
            """);

        WriteProfile("TestMfg", "machine/concrete_child.json", """
            {
              "name": "Concrete Child 0.4 nozzle",
              "inherits": "abstract_base",
              "instantiation": "true",
              "retraction_length": ["1.0"]
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        // Abstract profile filtered out, concrete child included
        profiles.Should().HaveCount(1);
        profiles[0].Name.Should().Be("Concrete Child 0.4 nozzle");

        // Child inherited gcode_flavor from abstract parent
        profiles[0].GcodeDialect.Should().Be("marlin");
        profiles[0].RetractionLength.Should().Be(1.0);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 6. Missing parent graceful handling
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MissingParent_DoesNotCrash_ProfileStillLoaded()
    {
        WriteManufacturerBundle("TestMfg", machineEntries: [
            ("Orphan Machine 0.4 nozzle", "machine/orphan.json"),
        ]);

        // Profile references a parent that doesn't exist
        WriteProfile("TestMfg", "machine/orphan.json", """
            {
              "name": "Orphan Machine 0.4 nozzle",
              "inherits": "nonexistent_parent",
              "instantiation": "true",
              "nozzle_diameter": ["0.4"],
              "printable_area": "0x0,220x0,220x220,0x220",
              "printable_height": "250",
              "retraction_length": ["1.0"]
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        // Should not throw
        var profiles = await service.ListAvailableMachineProfilesAsync();

        // Profile should still load with its own values (graceful degradation)
        profiles.Should().HaveCount(1);
        profiles[0].Name.Should().Be("Orphan Machine 0.4 nozzle");
        profiles[0].RetractionLength.Should().Be(1.0);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 7. Cycle detection: mutual inheritance should not infinite loop
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CycleDetection_DoesNotInfiniteLoop()
    {
        WriteManufacturerBundle("TestMfg", machineEntries: [
            ("Cycle A 0.4 nozzle", "machine/cycle_a.json"),
        ]);

        // A inherits B, B inherits A → cycle
        WriteProfile("TestMfg", "machine/cycle_a.json", """
            {
              "name": "Cycle A 0.4 nozzle",
              "inherits": "cycle_b",
              "instantiation": "true",
              "nozzle_diameter": ["0.4"],
              "printable_area": "0x0,220x0,220x220,0x220",
              "printable_height": "250",
              "retraction_length": ["1.0"]
            }
            """);

        WriteProfile("TestMfg", "machine/cycle_b.json", """
            {
              "name": "Cycle B",
              "inherits": "cycle_a",
              "instantiation": "false",
              "machine_max_speed_x": ["200"]
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        // Should not hang or throw - cycle detection via visited set
        var profiles = await service.ListAvailableMachineProfilesAsync();

        // Profile should still load (broken chain, but no crash)
        profiles.Should().HaveCount(1);
        profiles[0].Name.Should().Be("Cycle A 0.4 nozzle");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 8. Cross-manufacturer lookup
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CrossManufacturerLookup_FindsParentInDifferentManufacturer()
    {
        // BBL has a base filament profile that Elegoo's child inherits
        WriteManufacturerBundle("BBL",
            filamentEntries: [("fdm_filament_pla", "filament/fdm_filament_pla.json")]);

        WriteProfile("BBL", "filament/fdm_filament_pla.json", """
            {
              "name": "fdm_filament_pla",
              "instantiation": "false",
              "filament_type": ["PLA"],
              "filament_max_volumetric_speed": ["15"],
              "nozzle_temperature": ["220"],
              "nozzle_temperature_initial_layer": ["220"],
              "bed_temperature": ["60"]
            }
            """);

        WriteManufacturerBundle("Elegoo",
            filamentEntries: [("Elegoo PLA @Centauri", "filament/Elegoo PLA @Centauri.json")]);

        // Also need a machine profile in Elegoo for filament loading to work
        // (ListAvailableFilamentProfilesAsync calls EnsureMachinesCachedAsync)
        WriteProfile("Elegoo", "filament/Elegoo PLA @Centauri.json", """
            {
              "name": "Elegoo PLA @Centauri",
              "inherits": "fdm_filament_pla",
              "instantiation": "true",
              "filament_vendor": ["Elegoo"],
              "nozzle_temperature": ["210"]
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableFilamentProfilesAsync();

        // Should find one instantiatable profile (fdm_filament_pla is abstract)
        var elegooProfile = profiles.FirstOrDefault(p => p.Name == "Elegoo PLA @Centauri");
        elegooProfile.Should().NotBeNull();

        // Child override
        elegooProfile!.NozzleTemperature.Should().Be(210);

        // Inherited from BBL's base profile via cross-manufacturer lookup
        elegooProfile.BedTemperature.Should().Be(60);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 9. No inheritance - standalone profile loads correctly
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoInheritance_StandaloneProfileLoads()
    {
        WriteManufacturerBundle("TestMfg", machineEntries: [
            ("Standalone Printer 0.4 nozzle", "machine/standalone.json"),
        ]);

        WriteProfile("TestMfg", "machine/standalone.json", """
            {
              "name": "Standalone Printer 0.4 nozzle",
              "instantiation": "true",
              "nozzle_diameter": ["0.4"],
              "printable_area": "0x0,220x0,220x220,0x220",
              "printable_height": "250",
              "retraction_length": ["0.8"],
              "gcode_flavor": "klipper"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        profiles.Should().HaveCount(1);
        profiles[0].Name.Should().Be("Standalone Printer 0.4 nozzle");
        profiles[0].NozzleDiameter.Should().BeApproximately(0.4, 0.001);
        profiles[0].RetractionLength.Should().Be(0.8);
        profiles[0].GcodeDialect.Should().Be("klipper");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 10. Process profile inheritance works end-to-end
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessInheritance_ResolvesCorrectly()
    {
        WriteManufacturerBundle("TestMfg", processEntries: [
            ("Quality Child Profile", "process/quality_child.json"),
        ]);

        WriteProfile("TestMfg", "process/base_process.json", """
            {
              "name": "Base Process",
              "instantiation": "false",
              "layer_height": "0.20",
              "fill_density": "20",
              "print_speed": "100",
              "enable_support": "0"
            }
            """);

        WriteProfile("TestMfg", "process/quality_child.json", """
            {
              "name": "Quality Child Profile",
              "inherits": "base_process",
              "instantiation": "true",
              "layer_height": "0.12",
              "print_speed": "60"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableProcessProfilesAsync();

        profiles.Should().HaveCount(1);
        var profile = profiles[0];

        // Overridden by child
        profile.LayerHeight.Should().BeApproximately(0.12, 0.001);
        profile.PrintSpeed.Should().Be(60);

        // Inherited from parent
        profile.InfillPercentage.Should().Be(20);
        profile.Supports.Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 11. Settings dict contains merged keys from both parent and child
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MergedSettings_ContainsKeysFromParentAndChild()
    {
        WriteManufacturerBundle("TestMfg", machineEntries: [
            ("Settings Child 0.4 nozzle", "machine/settings_child.json"),
        ]);

        WriteProfile("TestMfg", "machine/settings_base.json", """
            {
              "name": "Settings Base",
              "instantiation": "false",
              "nozzle_diameter": ["0.4"],
              "printable_area": "0x0,220x0,220x220,0x220",
              "printable_height": "250",
              "parent_only_key": "parent_value",
              "shared_key": "from_parent"
            }
            """);

        WriteProfile("TestMfg", "machine/settings_child.json", """
            {
              "name": "Settings Child 0.4 nozzle",
              "inherits": "settings_base",
              "instantiation": "true",
              "child_only_key": "child_value",
              "shared_key": "from_child"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        profiles.Should().HaveCount(1);
        var settings = profiles[0].Settings;

        // Parent-only key inherited
        settings.Should().ContainKey("parent_only_key");
        settings["parent_only_key"].ToString().Should().Contain("parent_value");

        // Child-only key present
        settings.Should().ContainKey("child_only_key");
        settings["child_only_key"].ToString().Should().Contain("child_value");

        // Shared key has child's value (override)
        settings.Should().ContainKey("shared_key");
        settings["shared_key"].ToString().Should().Contain("from_child");
    }

    // ──────────────────────────────────────────────────────────────────────
    // 12. Parent in subdirectory: child references parent in same subfolder
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParentInSubdirectory_FoundViaFilesystemScan()
    {
        WriteManufacturerBundle("TestMfg", machineEntries: [
            ("Sub Child 0.4 nozzle", "machine/submodel/sub_child.json"),
        ]);

        // Parent is in the machine/ root, child is in machine/submodel/
        WriteProfile("TestMfg", "machine/sub_base.json", """
            {
              "name": "Sub Base",
              "instantiation": "false",
              "nozzle_diameter": ["0.4"],
              "printable_area": "0x0,220x0,220x220,0x220",
              "printable_height": "250",
              "gcode_flavor": "marlin2"
            }
            """);

        WriteProfile("TestMfg", "machine/submodel/sub_child.json", """
            {
              "name": "Sub Child 0.4 nozzle",
              "inherits": "sub_base",
              "instantiation": "true",
              "retraction_length": ["0.9"]
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        profiles.Should().HaveCount(1);
        var profile = profiles[0];

        // Inherited from parent in different subdirectory
        profile.GcodeDialect.Should().Be("marlin2");
        profile.NozzleDiameter.Should().BeApproximately(0.4, 0.001);

        // Own value
        profile.RetractionLength.Should().Be(0.9);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (Directory.Exists(_profilesRoot))
        {
            Directory.Delete(_profilesRoot, recursive: true);
        }
    }

    /// <summary>
    /// Writes a manufacturer bundle JSON listing machine, filament, and/or process entries.
    /// </summary>
    private void WriteManufacturerBundle(
        string manufacturer,
        (string name, string subPath)[]? machineEntries = null,
        (string name, string subPath)[]? filamentEntries = null,
        (string name, string subPath)[]? processEntries = null)
    {
        string manufacturerDir = Path.Combine(_profilesRoot, manufacturer);
        Directory.CreateDirectory(manufacturerDir);

        string machineJson = FormatBundleEntries(machineEntries);
        string filamentJson = FormatBundleEntries(filamentEntries);
        string processJson = FormatBundleEntries(processEntries);

        string bundlePath = Path.Combine(_profilesRoot, manufacturer + ".json");
        File.WriteAllText(bundlePath, $$"""
            {
              "name": "{{manufacturer}}",
              "version": "1.0",
              "description": "test",
              "machine_model_list": [],
              "machine_list": [{{machineJson}}],
              "filament_list": [{{filamentJson}}],
              "process_list": [{{processJson}}]
            }
            """);
    }

    private static string FormatBundleEntries((string name, string subPath)[]? entries)
    {
        if (entries == null || entries.Length == 0)
        {
            return "";
        }

        return string.Join(",", entries.Select(e =>
            $$"""{"name":"{{e.name}}","sub_path":"{{e.subPath}}"}"""));
    }

    /// <summary>
    /// Writes a profile JSON file under the manufacturer directory at the given sub-path.
    /// </summary>
    private void WriteProfile(string manufacturer, string subPath, string content)
    {
        string profilePath = Path.Combine(_profilesRoot, manufacturer, subPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        File.WriteAllText(profilePath, content);
    }
}
