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
        Dictionary<string, string> settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement);
        return OrcaSlicingPipelineService.SettingsDictToNativeJson(settings);
    }

    [Fact]
    public void ArrayValues_StoredAsJsonText_WrittenAsNativeArrays()
    {
        // Arrays are stored as raw JSON text strings in Dictionary<string, string>
        var dict = new Dictionary<string, string>
        {
            ["nozzle_diameter"] = "[\"0.4\"]",
            ["printable_area"] = "[\"0x0\",\"300x0\",\"300x300\",\"0x300\"]",
            ["post_process"] = "[]",
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
    public void ProcessProfile_StringValues_NotDoubleQuoted()
    {
        string json = RoundTrip(SampleProcessProfile);

        using JsonDocument output = JsonDocument.Parse(json);
        JsonElement root = output.RootElement;

        root.GetProperty("accel_to_decel_factor").GetString().Should().Be("50%");
        root.GetProperty("accel_to_decel_enable").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("accel_to_decel_enable").GetInt64().Should().Be(1);
        root.GetProperty("adaptive_layer_height").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("adaptive_layer_height").GetInt64().Should().Be(0);
        root.GetProperty("bottom_shell_layers").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("bottom_shell_layers").GetInt64().Should().Be(3);
        root.GetProperty("bridge_flow").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("bridge_flow").GetDouble().Should().Be(0.95);
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

        root.GetProperty("adaptive_bed_mesh_margin").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("adaptive_bed_mesh_margin").GetInt64().Should().Be(0);
        root.GetProperty("auxiliary_fan").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("auxiliary_fan").GetInt64().Should().Be(1);
        root.GetProperty("z_offset").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("z_offset").GetInt64().Should().Be(0);
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
        json.Should().Contain("\"accel_to_decel_enable\": 1");
        json.Should().Contain("\"default_acceleration\": 10000");
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
            fileContent.Should().Contain("\"default_acceleration\": 10000");
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
}
