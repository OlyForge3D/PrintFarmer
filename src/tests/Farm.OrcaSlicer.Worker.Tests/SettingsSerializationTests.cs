using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Tests the REAL production serialization path:
/// OrcaProfilesService.SerializeElementToDict → OrcaSlicingPipelineService.SettingsDictToNativeJson.
/// These are the actual methods that run in the container.
/// </summary>
public class SettingsSerializationTests
{
    // Minimal OrcaSlicer process profile with representative value types
    private const string SampleProcessProfile = """
        {
            "type": "process",
            "name": "0.20mm Standard @Phrozen Arco 0.4 nozzle",
            "accel_to_decel_enable": "1",
            "accel_to_decel_factor": "50%",
            "adaptive_layer_height": "0",
            "bottom_shell_layers": "3",
            "bridge_acceleration": "50%",
            "bridge_density": "100%",
            "bridge_flow": "0.95",
            "compatible_printers": [
                "Phrozen Arco 0.4 nozzle"
            ],
            "compatible_printers_condition": "",
            "default_acceleration": "10000",
            "sparse_infill_density": "15%",
            "sparse_infill_pattern": "crosshatch",
            "post_process": [],
            "wiping_volumes_extruders": [
                "70",
                "70"
            ]
        }
        """;

    private const string SampleMachineProfile = """
        {
            "type": "machine",
            "name": "Phrozen Arco 0.4 nozzle",
            "printer_settings_id": "Phrozen Arco 0.4 nozzle",
            "printer_model": "Phrozen Arco",
            "printer_variant": "0.4",
            "nozzle_diameter": [
                "0.4"
            ],
            "adaptive_bed_mesh_margin": "0",
            "auxiliary_fan": "1",
            "bed_exclude_area": [],
            "bed_mesh_max": "0,0",
            "printable_area": [
                "0x0",
                "300x0",
                "300x300",
                "0x300"
            ],
            "z_offset": "0",
            "gcode_flavor": "klipper"
        }
        """;

    /// <summary>
    /// Helper: runs the REAL production code path end-to-end.
    /// </summary>
    private static string RoundTrip(string inputJson)
    {
        using JsonDocument doc = JsonDocument.Parse(inputJson);
        Dictionary<string, object> settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement);
        return OrcaSlicingPipelineService.SettingsDictToNativeJson(settings);
    }

    [Fact]
    public void SerializeElementToDict_ArrayValues_StoredAsTypedLists()
    {
        const string json = """{"nozzle_diameter": ["0.4"], "printable_area": ["0x0","300x0"], "post_process": []}""";
        using JsonDocument doc = JsonDocument.Parse(json);
        Dictionary<string, object> settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement);

        settings["nozzle_diameter"].Should().BeOfType<List<string>>()
            .Which.Should().ContainSingle().Which.Should().Be("0.4");
        settings["printable_area"].Should().BeOfType<List<string>>()
            .Which.Should().HaveCount(2);
        settings["post_process"].Should().BeOfType<List<string>>()
            .Which.Should().BeEmpty();
    }

    [Fact]
    public void SerializeElementToDict_ScalarValues_StoredAsStrings()
    {
        const string json = """{"name": "test", "auxiliary_fan": true, "adaptive_layer_height": false, "z_offset": 0.5}""";
        using JsonDocument doc = JsonDocument.Parse(json);
        Dictionary<string, object> settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement);

        settings["name"].Should().Be("test");
        settings["auxiliary_fan"].Should().Be("1");
        settings["adaptive_layer_height"].Should().Be("0");
        settings["z_offset"].Should().Be("0.5");
    }

    [Fact]
    public void SerializeElementToDict_MotionLimits_StoredAsTypedArrays()
    {
        const string json = """
        {
            "machine_max_speed_x": ["500", "200"],
            "machine_max_acceleration_x": ["20000", "20000"],
            "machine_max_jerk_x": ["9", "9"],
            "retraction_length": ["0.8"]
        }
        """;
        using JsonDocument doc = JsonDocument.Parse(json);
        Dictionary<string, object> settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement);

        var speedX = settings["machine_max_speed_x"].Should().BeOfType<List<string>>().Subject;
        speedX.Should().HaveCount(2);
        speedX[0].Should().Be("500");
        speedX[1].Should().Be("200");

        var accelX = settings["machine_max_acceleration_x"].Should().BeOfType<List<string>>().Subject;
        accelX[0].Should().Be("20000");
    }

    [Fact]
    public void ArrayValues_WrittenAsNativeArrays()
    {
        var dict = new Dictionary<string, object>
        {
            ["nozzle_diameter"] = new List<string> { "0.4" },
            ["printable_area"] = new List<string> { "0x0", "300x0", "300x300", "0x300" },
            ["post_process"] = new List<string>(),
            ["scalar_value"] = "50%",
        };

        string json = OrcaSlicingPipelineService.SettingsDictToNativeJson(dict);
        using var output = JsonDocument.Parse(json);
        var root = output.RootElement;

        root.GetProperty("nozzle_diameter").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("nozzle_diameter")[0].GetString().Should().Be("0.4");
        root.GetProperty("printable_area").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("printable_area").GetArrayLength().Should().Be(4);
        root.GetProperty("post_process").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("post_process").GetArrayLength().Should().Be(0);
        root.GetProperty("scalar_value").GetString().Should().Be("50%");
    }

    [Fact]
    public void FilamentColourOverride_WrittenAsNativeJsonArray()
    {
        // Mirrors how the worker injects a per-slice filament colour override:
        // Settings["filament_colour"] = new List<string> { "#FF8000" }.
        // OrcaSlicer expects filament_colour as a JSON array, not a scalar string.
        var dict = new Dictionary<string, object>
        {
            ["filament_colour"] = new List<string> { "#FF8000" },
        };

        string json = OrcaSlicingPipelineService.SettingsDictToNativeJson(dict);
        using var output = JsonDocument.Parse(json);
        JsonElement colour = output.RootElement.GetProperty("filament_colour");

        colour.ValueKind.Should().Be(JsonValueKind.Array);
        colour.GetArrayLength().Should().Be(1);
        colour[0].GetString().Should().Be("#FF8000");
    }

    [Fact]
    public void ProcessProfile_StringValues_NotDoubleQuoted()
    {
        string json = RoundTrip(SampleProcessProfile);

        using JsonDocument output = JsonDocument.Parse(json);
        JsonElement root = output.RootElement;

        root.GetProperty("accel_to_decel_factor").GetString().Should().Be("50%");
        root.GetProperty("accel_to_decel_enable").GetString().Should().Be("1");
        root.GetProperty("adaptive_layer_height").GetString().Should().Be("0");
        root.GetProperty("bottom_shell_layers").GetString().Should().Be("3");
        root.GetProperty("bridge_flow").GetString().Should().Be("0.95");
        root.GetProperty("sparse_infill_pattern").GetString().Should().Be("crosshatch");
        root.GetProperty("compatible_printers_condition").GetString().Should().Be("");
        root.GetProperty("type").GetString().Should().Be("process");
    }

    [Fact]
    public void ProcessProfile_PercentageValues_PreservedAsIs()
    {
        string json = RoundTrip(SampleProcessProfile);

        using JsonDocument output = JsonDocument.Parse(json);
        JsonElement root = output.RootElement;

        root.GetProperty("accel_to_decel_factor").GetString().Should().Be("50%");
        root.GetProperty("bridge_acceleration").GetString().Should().Be("50%");
        root.GetProperty("bridge_density").GetString().Should().Be("100%");
        root.GetProperty("sparse_infill_density").GetString().Should().Be("15%");
    }

    [Fact]
    public void ProcessProfile_Arrays_RemainNativeJsonArrays()
    {
        string json = RoundTrip(SampleProcessProfile);

        using JsonDocument output = JsonDocument.Parse(json);
        JsonElement root = output.RootElement;

        root.GetProperty("compatible_printers").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("compatible_printers")[0].GetString().Should().Be("Phrozen Arco 0.4 nozzle");

        root.GetProperty("post_process").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("post_process").GetArrayLength().Should().Be(0);

        root.GetProperty("wiping_volumes_extruders").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("wiping_volumes_extruders")[0].GetString().Should().Be("70");
    }

    [Fact]
    public void MachineProfile_AllValueTypes_CorrectFormat()
    {
        string json = RoundTrip(SampleMachineProfile);

        using JsonDocument output = JsonDocument.Parse(json);
        JsonElement root = output.RootElement;

        root.GetProperty("adaptive_bed_mesh_margin").GetString().Should().Be("0");
        root.GetProperty("auxiliary_fan").GetString().Should().Be("1");
        root.GetProperty("z_offset").GetString().Should().Be("0");
        root.GetProperty("gcode_flavor").GetString().Should().Be("klipper");

        root.GetProperty("nozzle_diameter").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("nozzle_diameter")[0].GetString().Should().Be("0.4");

        root.GetProperty("printable_area").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("printable_area")[0].GetString().Should().Be("0x0");
    }

    [Fact]
    public void Output_NoBackslashQuotes_InRawJson()
    {
        string json = RoundTrip(SampleProcessProfile);

        json.Should().NotContain("\\u0022", "no unicode-escaped quotes");
        json.Should().NotContain("\\\"", "no backslash-escaped quotes inside values");
    }

    [Fact]
    public void Output_MatchesNativeOrcaSlicerFormat()
    {
        string json = RoundTrip(SampleProcessProfile);

        json.Should().Contain("\"accel_to_decel_factor\": \"50%\"");
        json.Should().Contain("\"accel_to_decel_enable\": \"1\"");
        json.Should().Contain("\"default_acceleration\": \"10000\"");
        json.Should().Contain("\"sparse_infill_pattern\": \"crosshatch\"");
    }

    [Fact]
    public void Output_WrittenToFile_MatchesNativeFormat()
    {
        // Simulate the EXACT file-writing path the worker uses
        string json = RoundTrip(SampleProcessProfile);

        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, json);
            string fileContent = File.ReadAllText(tempFile);

            // Verify the file content byte-for-byte
            fileContent.Should().Contain("\"accel_to_decel_factor\": \"50%\"");
            fileContent.Should().Contain("\"default_acceleration\": \"10000\"");
            fileContent.Should().NotContain("\\u0022");
            fileContent.Should().NotContain("\\\"");

            // Verify it's valid JSON that round-trips cleanly
            using JsonDocument reparsed = JsonDocument.Parse(fileContent);
            reparsed.RootElement.GetProperty("accel_to_decel_factor").GetString().Should().Be("50%");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ── issue #1768: machine `inherits` must name the system preset ───────────
    //
    // OrcaSlicer gates process/machine compatibility on the machine document's *system preset
    // name*, which it reads from `inherits` — never from `name`. A flattened stock profile carries
    // the vendor bundle's internal base there (e.g. "fdm_machine_common"), which no process profile
    // lists in `compatible_printers`, so OrcaSlicer rejected every stock submission with
    // CLI_PROCESS_NOT_COMPATIBLE (-17) about a second in, before slicing any geometry.
    //
    // Proven against the live deployment: with `inherits` left as "fdm_machine_common" the CLI exits
    // 239 (-17); changing only that one value to "Phrozen Arco 0.4 nozzle" exits 0 and produces a
    // 12 MB plate_1.gcode. These tests pin that value on the document the worker writes.

    [Fact(DisplayName = "A flattened stock machine profile declares the system preset it snapshots, not the vendor base it inherited from")]
    public void WithSystemPresetInherits_StockProfile_ReplacesVendorBaseWithPresetName()
    {
        using JsonDocument doc = JsonDocument.Parse(SampleMachineProfile);
        Dictionary<string, object> settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement);
        settings["inherits"] = "fdm_machine_common";

        Dictionary<string, object> corrected =
            OrcaSlicingPipelineService.WithSystemPresetInherits(settings, "Phrozen Arco 0.4 nozzle");

        corrected["inherits"].Should().Be(
            "Phrozen Arco 0.4 nozzle",
            "OrcaSlicer matches compatible_printers against the machine's inherits value");
    }

    [Fact(DisplayName = "The corrected inherits value survives serialization and matches the process profile's compatible_printers entry")]
    public void WithSystemPresetInherits_SerializedDocument_MatchesCompatiblePrintersEntry()
    {
        using JsonDocument machineDoc = JsonDocument.Parse(SampleMachineProfile);
        Dictionary<string, object> machineSettings =
            OrcaProfilesService.SerializeElementToDict(machineDoc.RootElement);
        machineSettings["inherits"] = "fdm_machine_common";

        string machineJson = OrcaSlicingPipelineService.SettingsDictToNativeJson(
            OrcaSlicingPipelineService.WithSystemPresetInherits(machineSettings, "Phrozen Arco 0.4 nozzle"));

        using JsonDocument writtenMachine = JsonDocument.Parse(machineJson);
        string systemPresetName = writtenMachine.RootElement.GetProperty("inherits").GetString()!;

        // The exact comparison OrcaSlicer performs: every compatible_printers entry vs the
        // machine's system preset name. At least one must match or it exits -17.
        using JsonDocument processDoc = JsonDocument.Parse(SampleProcessProfile);
        string[] compatiblePrinters = processDoc.RootElement
            .GetProperty("compatible_printers")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();

        compatiblePrinters.Should().Contain(systemPresetName);
        systemPresetName.Should().NotBe("fdm_machine_common", "the vendor base is never a compatible printer");
    }

    [Fact(DisplayName = "A machine profile with no inherits key gains one, since an absent system preset name matches nothing either")]
    public void WithSystemPresetInherits_MissingInheritsKey_IsAdded()
    {
        using JsonDocument doc = JsonDocument.Parse(SampleMachineProfile);
        Dictionary<string, object> settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement);
        settings.Remove("inherits");

        Dictionary<string, object> corrected =
            OrcaSlicingPipelineService.WithSystemPresetInherits(settings, "Phrozen Arco 0.4 nozzle");

        corrected.Should().ContainKey("inherits");
        corrected["inherits"].Should().Be("Phrozen Arco 0.4 nozzle");
    }

    [Fact(DisplayName = "An unknown preset name leaves the settings untouched rather than writing a meaningless inherits value")]
    public void WithSystemPresetInherits_NoPresetName_LeavesSettingsUnchanged()
    {
        using JsonDocument doc = JsonDocument.Parse(SampleMachineProfile);
        Dictionary<string, object> settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement);
        settings["inherits"] = "fdm_machine_common";

        OrcaSlicingPipelineService.WithSystemPresetInherits(settings, null)["inherits"]
            .Should().Be("fdm_machine_common");
        OrcaSlicingPipelineService.WithSystemPresetInherits(settings, "   ")["inherits"]
            .Should().Be("fdm_machine_common");
    }

    [Fact(DisplayName = "Correcting inherits does not mutate the caller's settings bag or disturb the other keys")]
    public void WithSystemPresetInherits_DoesNotMutateSourceOrDropKeys()
    {
        using JsonDocument doc = JsonDocument.Parse(SampleMachineProfile);
        Dictionary<string, object> settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement);
        settings["inherits"] = "fdm_machine_common";
        int originalCount = settings.Count;

        Dictionary<string, object> corrected =
            OrcaSlicingPipelineService.WithSystemPresetInherits(settings, "Phrozen Arco 0.4 nozzle");

        // The resolved profile is shared state; correcting the emitted document must not edit it.
        settings["inherits"].Should().Be("fdm_machine_common");
        corrected.Should().HaveCount(originalCount);
        corrected["name"].Should().Be("Phrozen Arco 0.4 nozzle");
        corrected["printer_model"].Should().Be("Phrozen Arco");
        corrected["gcode_flavor"].Should().Be("klipper");
        corrected["nozzle_diameter"].Should().BeOfType<List<string>>();
    }
}
