using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Dtos;
using Farm.Testing.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests.Contracts;

/// <summary>
/// Native-Orca wire-contract corpus for issue #2238. Drives REAL production profile parsing
/// (<see cref="OrcaProfilesService"/>, the same code the standalone worker uses to answer
/// <c>GET /api/profiles</c>) over genuine, verbatim OrcaSlicer bundle content, and captures the
/// resulting raw <c>snake_case</c> settings bag — never a PrintFarmer-mapped DTO field — to
/// <see cref="WireContractCorpusPaths.NativeSlicerRoot"/>.
/// </summary>
/// <remarks>
/// This corpus is DELIBERATELY kept in a directory fully separate from
/// <see cref="WireContractCorpusPaths.ApiRoot"/> and uses no shared normalization/camelCase
/// helper with the PrintFarmer DTO tests, per the issue's explicit instruction: "A SEPARATE
/// native-slicer corpus for Orca snake_case payloads (compatible_printers etc.) — never merged
/// with PrintFarmer DTO fixtures." <see cref="FilamentProfileDto.Settings"/> is populated by
/// <c>OrcaProfilesService</c> from <c>SerializeElementToDict(root)</c> over the fully-resolved
/// (post-<c>inherits</c>-merge) profile JSON, so its keys are the exact native Orca field names
/// (<c>filament_flow_ratio</c>, <c>compatible_printers</c>, etc.) with no naming transformation
/// applied anywhere in this pipeline.
/// </remarks>
public sealed class NativeSlicerCorpusTests : IDisposable
{
    // Verbatim content of the real, vendored OrcaSlicer bundle file checked into this repo at
    // sample_profiles/orcaslicer/Prusa/filament/Prusa Generic PLA.json — copied here (not
    // read from disk at test time) so the fixture is reproducible independent of that file's
    // location and so a future edit to the vendored sample doesn't silently change this corpus
    // without a reviewed test diff.
    private const string RealPrusaGenericPlaFilamentJson = """
        {
        	"type": "filament",
        	"name": "Prusa Generic PLA",
        	"inherits": "fdm_filament_pla",
        	"from": "system",
        	"setting_id": "pKHhR3Hx6AUoyIO3",
        	"instantiation": "true",
        	"filament_flow_ratio": [
        		"0.98"
        	],
        	"filament_max_volumetric_speed": [
        		"12"
        	],
        	"slow_down_layer_time": [
        		"8"
        	],
        	"compatible_printers": [
        		"Prusa MK3S 0.25 nozzle",
        		"Prusa MK3S 0.4 nozzle",
        		"Prusa MK3S 0.6 nozzle",
        		"Prusa MK3S 0.8 nozzle",
        		"Prusa MINI 0.25 nozzle",
        		"Prusa MINI 0.4 nozzle",
        		"Prusa MINI 0.6 nozzle",
        		"Prusa MINI 0.8 nozzle"
        	]
        }
        """;

    private readonly string _profilesRoot;

    public NativeSlicerCorpusTests()
    {
        _profilesRoot = Path.Join(Path.GetTempPath(), "pfarm-native-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profilesRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_profilesRoot))
        {
            Directory.Delete(_profilesRoot, recursive: true);
        }
    }

    /// <summary>
    /// Populated variant: the real, verbatim Prusa "Generic PLA" filament bundle, parsed by the
    /// real <see cref="OrcaProfilesService"/>. Proves the native <c>compatible_printers</c> array
    /// (a populated collection of 8 printer names) and other snake_case native keys
    /// (<c>filament_flow_ratio</c> etc.) survive real production parsing untouched.
    /// </summary>
    [Fact]
    public async Task ListAvailableFilamentProfilesAsync_RealPrusaGenericPla_CapturesNativeSnakeCaseSettings()
    {
        WriteManufacturerBundle("Prusa", filamentEntries: [("Prusa Generic PLA", "filament/prusa_generic_pla.json")]);
        WriteProfile("Prusa", "filament/prusa_generic_pla.json", RealPrusaGenericPlaFilamentJson);
        WriteProfile("Prusa", "filament/fdm_filament_pla.json", """
            {
              "type": "filament",
              "name": "fdm_filament_pla",
              "instantiation": "false",
              "filament_type": ["PLA"],
              "temperature": ["210"],
              "bed_temperature": ["60"]
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        IList<FilamentProfileDto> profiles = await service.ListAvailableFilamentProfilesAsync();
        FilamentProfileDto profile = Assert.Single(profiles);

        Assert.Equal(8, profile.CompatiblePrinters.Count);
        Assert.Contains("Prusa MK3S 0.4 nozzle", profile.CompatiblePrinters);

        string json = JsonSerializer.Serialize(profile.Settings, new JsonSerializerOptions { WriteIndented = true });
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            corpusRoot: WireContractCorpusPaths.NativeSlicerRoot,
            relativePath: "filament/prusa-generic-pla.populated.json",
            endpoint: "OrcaProfilesService.ListAvailableFilamentProfilesAsync (native settings bag)",
            producingTest: "Farm.OrcaSlicer.Worker.Tests.Contracts.NativeSlicerCorpusTests.ListAvailableFilamentProfilesAsync_RealPrusaGenericPla_CapturesNativeSnakeCaseSettings",
            schemaVersion: "1.0",
            actualJson: json);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("compatible_printers", out _),
            "the native settings bag must expose the field under its real snake_case Orca key, never a camelCase-transformed one");
        Assert.True(document.RootElement.TryGetProperty("filament_flow_ratio", out _));
    }

    /// <summary>
    /// Minimal variant: a filament profile with no <c>compatible_printers</c> key at all (legal —
    /// OrcaSlicer profiles commonly rely on <c>compatible_printers_condition</c> instead). Proves
    /// the missing-key case is preserved as a genuinely absent key in the native settings bag,
    /// not defaulted to an empty array within the raw settings dictionary itself (the strongly
    /// typed <see cref="FilamentProfileDto.CompatiblePrinters"/> property does default to an empty
    /// list, but that is a PrintFarmer-side convenience, not a claim about the native payload).
    /// </summary>
    [Fact]
    public async Task ListAvailableFilamentProfilesAsync_MinimalProfile_OmitsCompatiblePrintersKeyEntirely()
    {
        WriteManufacturerBundle("Acme", filamentEntries: [("Acme Minimal PLA", "filament/minimal.json")]);
        WriteProfile("Acme", "filament/minimal.json", """
            {
              "type": "filament",
              "name": "Acme Minimal PLA",
              "instantiation": "true",
              "filament_type": ["PLA"]
            }
            """);

        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);
        IList<FilamentProfileDto> profiles = await service.ListAvailableFilamentProfilesAsync();
        FilamentProfileDto profile = Assert.Single(profiles);

        Assert.Empty(profile.CompatiblePrinters);

        string json = JsonSerializer.Serialize(profile.Settings, new JsonSerializerOptions { WriteIndented = true });
        await WireContractFixtureWriter.CaptureOrVerifyAsync(
            corpusRoot: WireContractCorpusPaths.NativeSlicerRoot,
            relativePath: "filament/minimal.missing-compatible-printers.json",
            endpoint: "OrcaProfilesService.ListAvailableFilamentProfilesAsync (native settings bag)",
            producingTest: "Farm.OrcaSlicer.Worker.Tests.Contracts.NativeSlicerCorpusTests.ListAvailableFilamentProfilesAsync_MinimalProfile_OmitsCompatiblePrintersKeyEntirely",
            schemaVersion: "1.0",
            actualJson: json);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("compatible_printers", out _),
            "compatible_printers is genuinely absent from the native profile file — the raw settings bag must not fabricate the key");
    }

    private void WriteManufacturerBundle(
        string manufacturer,
        (string name, string subPath)[]? machineEntries = null,
        (string name, string subPath)[]? filamentEntries = null,
        (string name, string subPath)[]? processEntries = null)
    {
        string manufacturerDir = Path.Join(_profilesRoot, manufacturer);
        Directory.CreateDirectory(manufacturerDir);

        string machineJson = FormatBundleEntries(machineEntries);
        string filamentJson = FormatBundleEntries(filamentEntries);
        string processJson = FormatBundleEntries(processEntries);

        string bundlePath = Path.Join(_profilesRoot, manufacturer + ".json");
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

    private void WriteProfile(string manufacturer, string subPath, string content)
    {
        string fullPath = Path.Join(_profilesRoot, manufacturer, subPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }
}
