using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Tests the exact serialization path: JSON profile → SerializeElementToDict → SettingsDictToNativeJson → file output.
/// Validates the output matches native OrcaSlicer profile format.
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
    /// Reproduces the exact code path: parse JSON → SerializeElementToDict → SettingsDictToNativeJson.
    /// These are private static methods, so we duplicate the logic here for testing.
    /// </summary>
    private static Dictionary<string, object> SerializeElementToDict(JsonElement elem)
    {
        Dictionary<string, object> dict = [];
        if (elem.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in elem.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.Array => prop.Value.Clone(),
                    JsonValueKind.True => "1",
                    JsonValueKind.False => "0",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    _ => prop.Value.GetRawText()
                };
            }
        }

        return dict;
    }

    private static string SettingsDictToNativeJson(Dictionary<string, object>? settings)
    {
        if (settings == null || settings.Count == 0)
        {
            return "{}";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, object> kvp in settings)
            {
                writer.WritePropertyName(kvp.Key);

                if (kvp.Value is JsonElement jsonElem)
                {
                    jsonElem.WriteTo(writer);
                }
                else
                {
                    writer.WriteStringValue(kvp.Value?.ToString() ?? string.Empty);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void ProcessProfile_StringValues_NotDoubleQuoted()
    {
        using JsonDocument doc = JsonDocument.Parse(SampleProcessProfile);
        Dictionary<string, object> settings = SerializeElementToDict(doc.RootElement);
        string json = SettingsDictToNativeJson(settings);

        // Parse the output and verify values are clean strings, not double-quoted
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
        using JsonDocument doc = JsonDocument.Parse(SampleProcessProfile);
        Dictionary<string, object> settings = SerializeElementToDict(doc.RootElement);
        string json = SettingsDictToNativeJson(settings);

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
        using JsonDocument doc = JsonDocument.Parse(SampleProcessProfile);
        Dictionary<string, object> settings = SerializeElementToDict(doc.RootElement);
        string json = SettingsDictToNativeJson(settings);

        using JsonDocument output = JsonDocument.Parse(json);
        JsonElement root = output.RootElement;

        // compatible_printers should be an array, not a string
        root.GetProperty("compatible_printers").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("compatible_printers")[0].GetString().Should().Be("Phrozen Arco 0.4 nozzle");

        // post_process should be an empty array
        root.GetProperty("post_process").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("post_process").GetArrayLength().Should().Be(0);

        // wiping_volumes_extruders should be an array of strings
        root.GetProperty("wiping_volumes_extruders").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("wiping_volumes_extruders")[0].GetString().Should().Be("70");
    }

    [Fact]
    public void MachineProfile_AllValueTypes_CorrectFormat()
    {
        using JsonDocument doc = JsonDocument.Parse(SampleMachineProfile);
        Dictionary<string, object> settings = SerializeElementToDict(doc.RootElement);
        string json = SettingsDictToNativeJson(settings);

        using JsonDocument output = JsonDocument.Parse(json);
        JsonElement root = output.RootElement;

        // String values
        root.GetProperty("adaptive_bed_mesh_margin").GetString().Should().Be("0");
        root.GetProperty("auxiliary_fan").GetString().Should().Be("1");
        root.GetProperty("z_offset").GetString().Should().Be("0");
        root.GetProperty("gcode_flavor").GetString().Should().Be("klipper");

        // Array values - nozzle_diameter
        root.GetProperty("nozzle_diameter").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("nozzle_diameter")[0].GetString().Should().Be("0.4");

        // printable_area - array of coordinate strings
        root.GetProperty("printable_area").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("printable_area")[0].GetString().Should().Be("0x0");
    }

    [Fact]
    public void Output_NoBackslashQuotes_InRawJson()
    {
        using JsonDocument doc = JsonDocument.Parse(SampleProcessProfile);
        Dictionary<string, object> settings = SerializeElementToDict(doc.RootElement);
        string json = SettingsDictToNativeJson(settings);

        // The raw JSON string should never contain \" inside values
        // (backslash-quote indicates double-quoting bug)
        json.Should().NotContain("\\u0022", "no unicode-escaped quotes");
        json.Should().NotContain("\\\"", "no backslash-escaped quotes inside values");
    }

    [Fact]
    public void Output_MatchesNativeOrcaSlicerFormat()
    {
        using JsonDocument doc = JsonDocument.Parse(SampleProcessProfile);
        Dictionary<string, object> settings = SerializeElementToDict(doc.RootElement);
        string json = SettingsDictToNativeJson(settings);

        // Spot-check that specific lines match OrcaSlicer native format
        json.Should().Contain("\"accel_to_decel_factor\": \"50%\"");
        json.Should().Contain("\"accel_to_decel_enable\": \"1\"");
        json.Should().Contain("\"default_acceleration\": \"10000\"");
        json.Should().Contain("\"sparse_infill_pattern\": \"crosshatch\"");
    }
}
