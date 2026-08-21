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
/// Regression tests for #1780: OrcaSlicer's Prusa CORE One / CORE One L bundle never sets
/// <c>nozzle_type</c> anywhere in the profile's inheritance chain, leaving a standard
/// profile and its HF sibling structurally identical (same nozzle diameter, same
/// printer variant) with only the display <c>name</c> to tell them apart. These tests
/// reproduce that exact shape — including the real bundle's quirk where the "standard"
/// profile inherits from its own HF sibling but overrides printer_notes/printer_model
/// back to non-HF — and assert that <see cref="Farm.Slicer.Module.Dtos.MachineProfileDto.IsHighFlowNozzle"/>
/// distinguishes them without any consumer needing to parse <c>name</c>.
/// </summary>
public sealed class HotendVariantDetectionTests : IDisposable
{
    private readonly string _profilesRoot;

    public HotendVariantDetectionTests()
    {
        _profilesRoot = Path.Join(Path.GetTempPath(), "pfarm-hf-detection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profilesRoot);
    }

    // ──────────────────────────────────────────────────────────────────────
    // 1. CORE-One-shaped bundle: nozzle_type absent everywhere in the chain;
    //    standard and HF 0.4 profiles share nozzle_diameter/printer_variant and
    //    are distinguished only by printer_notes/printer_model, which the
    //    "standard" profile explicitly overrides back to non-HF despite
    //    inheriting from its own HF sibling.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CoreOneShapedBundle_DistinguishesHfFromStandard_WithoutNozzleTypeOrName()
    {
        WriteManufacturerBundle("PrusaLike", [
            ("TestPrinter HF 0.4 nozzle", "machine/hf_0_4.json"),
            ("TestPrinter 0.4 nozzle", "machine/standard_0_4.json"),
            ("TestPrinter HF 0.5 nozzle", "machine/hf_0_5.json"),
        ]);

        // Base HF profile: declares the HF_NOZZLE marker and " HF" printer_model directly.
        WriteProfile("PrusaLike", "machine/hf_0_4.json", """
            {
              "name": "TestPrinter HF 0.4 nozzle",
              "instantiation": "true",
              "nozzle_diameter": ["0.4"],
              "printer_model": "TestPrinter HF",
              "printer_variant": "0.4",
              "printer_notes": "PRINTER_MODEL_TESTPRINTER\nHF_NOZZLE\nNO_TEMPLATES"
            }
            """);

        // "Standard" sibling inherits from the HF base (mirroring the real CORE One
        // bundle) but overrides printer_notes/printer_model back to non-HF values.
        // Same nozzle_diameter and printer_variant as its HF sibling — no nozzle_type
        // anywhere in either file.
        WriteProfile("PrusaLike", "machine/standard_0_4.json", """
            {
              "name": "TestPrinter 0.4 nozzle",
              "inherits": "TestPrinter HF 0.4 nozzle",
              "instantiation": "true",
              "nozzle_diameter": ["0.4"],
              "printer_model": "TestPrinter",
              "printer_variant": "0.4",
              "printer_notes": "PRINTER_MODEL_TESTPRINTER\nNO_TEMPLATES"
            }
            """);

        // A second HF nozzle-size variant that inherits printer_notes/printer_model from
        // the HF base without redeclaring them, matching the real 0.5/0.6/0.8 siblings.
        WriteProfile("PrusaLike", "machine/hf_0_5.json", """
            {
              "name": "TestPrinter HF 0.5 nozzle",
              "inherits": "TestPrinter HF 0.4 nozzle",
              "instantiation": "true",
              "nozzle_diameter": ["0.5"],
              "printer_variant": "0.5"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        profiles.Should().HaveCount(3);

        var standard = profiles.Single(p => p.Name == "TestPrinter 0.4 nozzle");
        var hf04 = profiles.Single(p => p.Name == "TestPrinter HF 0.4 nozzle");
        var hf05 = profiles.Single(p => p.Name == "TestPrinter HF 0.5 nozzle");

        // Neither profile has nozzle_type anywhere in its chain — matches the confirmed
        // CORE One root cause exactly (genuinely absent, not a parsing bug).
        standard.NozzleType.Should().BeNull();
        hf04.NozzleType.Should().BeNull();
        hf05.NozzleType.Should().BeNull();

        // The standard/HF 0.4 pair is structurally identical apart from the HF marker,
        // exactly like "Prusa CORE One 0.4 nozzle" vs "Prusa CORE One HF 0.4 nozzle".
        standard.NozzleDiameter.Should().Be(hf04.NozzleDiameter);
        standard.PrinterVariant.Should().Be(hf04.PrinterVariant);

        // Yet IsHighFlowNozzle correctly distinguishes them without parsing `name`.
        standard.IsHighFlowNozzle.Should().BeFalse();
        hf04.IsHighFlowNozzle.Should().BeTrue();
        hf05.IsHighFlowNozzle.Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 2. No regression: a profile with an explicit nozzle_type (MK4-shaped)
    //    still populates NozzleType and is correctly not flagged as HF.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExplicitNozzleType_StillPopulated_AndNotFlaggedAsHighFlow()
    {
        WriteManufacturerBundle("PrusaLike", [
            ("TestMK 0.4 nozzle", "machine/mk.json"),
        ]);

        WriteProfile("PrusaLike", "machine/mk.json", """
            {
              "name": "TestMK 0.4 nozzle",
              "instantiation": "true",
              "nozzle_diameter": ["0.4"],
              "printer_model": "TestMK",
              "nozzle_type": "hardened_steel"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        profiles.Should().HaveCount(1);
        var profile = profiles[0];

        profile.NozzleType.Should().Be("hardened_steel");
        profile.IsHighFlowNozzle.Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 3. printer_model " HF" suffix alone (no HF_NOZZLE marker) is sufficient.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrinterModelHfSuffix_WithoutNote_IsDetectedAsHighFlow()
    {
        WriteManufacturerBundle("PrusaLike", [
            ("SuffixOnly 0.4 nozzle", "machine/suffix_only.json"),
        ]);

        WriteProfile("PrusaLike", "machine/suffix_only.json", """
            {
              "name": "SuffixOnly 0.4 nozzle",
              "instantiation": "true",
              "nozzle_diameter": ["0.4"],
              "printer_model": "SuffixOnly HF",
              "printer_notes": "PRINTER_MODEL_SUFFIXONLY\nNO_TEMPLATES"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        profiles.Should().HaveCount(1);
        profiles[0].IsHighFlowNozzle.Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────
    // 4. Neither structural signal present: falls back to a whole-word "HF"
    //    token in the profile name.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoStructuralSignal_FallsBackToNameToken()
    {
        WriteManufacturerBundle("PrusaLike", [
            ("NoSignal HF 0.4 nozzle", "machine/no_signal_hf.json"),
            ("NoSignal 0.4 nozzle", "machine/no_signal_standard.json"),
        ]);

        WriteProfile("PrusaLike", "machine/no_signal_hf.json", """
            {
              "name": "NoSignal HF 0.4 nozzle",
              "instantiation": "true",
              "nozzle_diameter": ["0.4"],
              "printer_model": "NoSignal"
            }
            """);

        WriteProfile("PrusaLike", "machine/no_signal_standard.json", """
            {
              "name": "NoSignal 0.4 nozzle",
              "instantiation": "true",
              "nozzle_diameter": ["0.4"],
              "printer_model": "NoSignal"
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        var profiles = await service.ListAvailableMachineProfilesAsync();

        profiles.Should().HaveCount(2);
        profiles.Single(p => p.Name == "NoSignal HF 0.4 nozzle").IsHighFlowNozzle.Should().BeTrue();
        profiles.Single(p => p.Name == "NoSignal 0.4 nozzle").IsHighFlowNozzle.Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_profilesRoot))
        {
            Directory.Delete(_profilesRoot, recursive: true);
        }
    }

    /// <summary>
    /// Writes a manufacturer bundle JSON listing machine entries. Mirrors the helper in
    /// <c>OrcaProfilesServiceInheritanceTests</c>.
    /// </summary>
    private void WriteManufacturerBundle(string manufacturer, (string name, string subPath)[] machineEntries)
    {
        string manufacturerDir = Path.Join(_profilesRoot, manufacturer);
        Directory.CreateDirectory(manufacturerDir);

        string machineJson = string.Join(",", machineEntries.Select(e =>
            $$"""{"name":"{{e.name}}","sub_path":"{{e.subPath}}"}"""));

        string bundlePath = Path.Join(_profilesRoot, manufacturer + ".json");
        File.WriteAllText(bundlePath, $$"""
            {
              "name": "{{manufacturer}}",
              "version": "1.0",
              "description": "test",
              "machine_model_list": [],
              "machine_list": [{{machineJson}}],
              "filament_list": [],
              "process_list": []
            }
            """);
    }

    /// <summary>
    /// Writes a profile JSON file under the manufacturer directory at the given sub-path.
    /// </summary>
    private void WriteProfile(string manufacturer, string subPath, string content)
    {
        string profilePath = Path.Join(_profilesRoot, manufacturer, subPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        File.WriteAllText(profilePath, content);
    }
}
