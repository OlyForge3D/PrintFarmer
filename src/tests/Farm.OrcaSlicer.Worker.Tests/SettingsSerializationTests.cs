using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Dtos;
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

    // ── issue #1795: condition-only process profiles must satisfy OrcaSlicer's gate ────────────
    //
    // On the --load-settings path where both a machine and a process document are supplied,
    // CLI::run decides compatibility by iterating ONLY the process document's compatible_printers
    // array, comparing each entry against the machine's system preset name. It never evaluates
    // compatible_printers_condition there, and the empty-array auto-pass sits in a different
    // branch, so a profile expressing compatibility only through the condition — the entire Prusa
    // MK4S and CORE One family, ~74 selectable presets — can never satisfy the gate and every such
    // job exits CLI_PROCESS_NOT_COMPATIBLE (-17, surfacing as 239) about a second in.
    //
    // The machine's system preset name is `name` when the document's `from` is exactly "system",
    // and `inherits` otherwise (OrcaSlicer.cpp, CLI::run).

    /// <summary>A stock Prusa machine document: <c>from</c> is <c>"system"</c>.</summary>
    private const string PrusaMk4SMachineProfile = """
        {
            "type": "machine",
            "name": "Prusa MK4S 0.4 nozzle",
            "inherits": "fdm_machine_common_mk4s",
            "from": "system",
            "printer_model": "Prusa MK4S",
            "nozzle_diameter": [
                "0.4"
            ],
            "printer_notes": [
                "PRINTER_VENDOR_PRUSA3D\nPRINTER_MODEL_MK4S\nPG\nNO_TEMPLATES"
            ]
        }
        """;

    /// <summary>
    /// A stock Prusa process document, in the shape it has once its inheritance chain is resolved:
    /// an EMPTY <c>compatible_printers</c> array plus a condition. The source file declares no
    /// array at all, but the resolved document carries an empty one — 163 process profiles in the
    /// bundled library are in exactly this shape, and that empty list is what OrcaSlicer's gate
    /// iterates and finds nothing in.
    /// </summary>
    private const string PrusaMk4SProcessProfile = """
        {
            "type": "process",
            "name": "0.10mm FAST DETAIL @MK4S 0.4",
            "inherits": "0.15mm SPEED @MK4S 0.4",
            "from": "system",
            "compatible_printers": [],
            "compatible_printers_condition": "printer_notes=~/.*MK4S.*/ and nozzle_diameter[0]==0.4",
            "layer_height": "0.1"
        }
        """;

    private static MachineProfileDto MachineFrom(string json, string name)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return new MachineProfileDto
        {
            Name = name,
            Settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement),
        };
    }

    private static ProcessProfileDto ProcessFrom(string json, string name)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return new ProcessProfileDto
        {
            Name = name,
            Settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement),
        };
    }

    [Fact(DisplayName = "A condition-only process profile gains the machine it is paired with, so OrcaSlicer's gate has something to match")]
    public void ResolveProcessCompatiblePrinters_ConditionOnlyProfile_MaterializesTheMachine()
    {
        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                ProcessFrom(PrusaMk4SProcessProfile, "0.10mm FAST DETAIL @MK4S 0.4"),
                MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle"));

        resolution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.InjectedFromCondition);
        resolution.Settings["compatible_printers"].Should().BeOfType<List<string>>()
            .Which.Should().ContainSingle().Which.Should().Be("Prusa MK4S 0.4 nozzle");
    }

    [Fact(DisplayName = "The materialized entry survives serialization and matches the machine document's system preset name")]
    public void ResolveProcessCompatiblePrinters_SerializedDocuments_SatisfyTheGate()
    {
        MachineProfileDto machine = MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle");
        string machineJson = OrcaSlicingPipelineService.SettingsDictToNativeJson(machine.Settings);
        string processJson = OrcaSlicingPipelineService.SettingsDictToNativeJson(
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                ProcessFrom(PrusaMk4SProcessProfile, "0.10mm FAST DETAIL @MK4S 0.4"),
                machine).Settings);

        // The exact comparison OrcaSlicer performs, run over the serialized documents.
        using JsonDocument writtenMachine = JsonDocument.Parse(machineJson);
        using JsonDocument writtenProcess = JsonDocument.Parse(processJson);

        // `from` is "system" here, so the system preset name is the document's `name` — NOT its
        // `inherits`, which names the vendor bundle's internal base.
        string systemPresetName = writtenMachine.RootElement.GetProperty("name").GetString()!;
        systemPresetName.Should().Be("Prusa MK4S 0.4 nozzle");

        writtenProcess.RootElement
            .GetProperty("compatible_printers")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Contain(
                systemPresetName,
                "otherwise OrcaSlicer exits -17 (CLI_PROCESS_NOT_COMPATIBLE) without slicing");

        OrcaSlicingPipelineService.DescribeUnsatisfiableCompatibilityGate(machineJson, processJson)
            .Should().BeNull();
    }

    [Theory(DisplayName = "Every condition-only family in the bundle — MK4S, CORE One and CORE One L — is materialized")]
    [InlineData("PRINTER_MODEL_MK4S", "printer_notes=~/.*MK4S.*/ and nozzle_diameter[0]==0.4", "0.4")]
    [InlineData("PRINTER_MODEL_COREONE ", "printer_notes=~/.*PRINTER_MODEL_COREONE[^_a-zA-Z0-9].*/ and nozzle_diameter[0]==0.6", "0.6")]
    [InlineData("PRINTER_MODEL_COREONE_L ", "printer_notes=~/.*PRINTER_MODEL_COREONE_L[^_a-zA-Z0-9].*/ and nozzle_diameter[0]==0.25", "0.25")]
    public void ResolveProcessCompatiblePrinters_EachAffectedFamily_IsMaterialized(
        string printerNotesKeyword,
        string condition,
        string nozzleDiameter)
    {
        var machine = new MachineProfileDto
        {
            Name = $"Prusa {printerNotesKeyword.Trim()} {nozzleDiameter} nozzle",
            Settings = new Dictionary<string, object>
            {
                ["name"] = $"Prusa {printerNotesKeyword.Trim()} {nozzleDiameter} nozzle",
                ["from"] = "system",
                ["nozzle_diameter"] = new List<string> { nozzleDiameter },
                ["printer_notes"] = new List<string> { $"PRINTER_VENDOR_PRUSA3D\n{printerNotesKeyword}\nPG" },
            },
        };
        var process = new ProcessProfileDto
        {
            Name = "condition-only",
            Settings = new Dictionary<string, object>
            {
                ["compatible_printers_condition"] = condition,
                ["layer_height"] = "0.1",
            },
        };

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(process, machine);

        resolution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.InjectedFromCondition);
        resolution.Settings["compatible_printers"].Should().BeOfType<List<string>>()
            .Which.Should().ContainSingle().Which.Should().Be(machine.Name);
    }

    [Fact(DisplayName = "A pairing whose condition does not hold is left unsatisfied rather than forced through")]
    public void ResolveProcessCompatiblePrinters_ConditionDoesNotHold_LeavesTheGateUnsatisfied()
    {
        // Same MK4S process profile, but a 0.6 nozzle machine — the condition pins 0.4.
        MachineProfileDto machine = MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.6 nozzle");
        machine.Settings!["nozzle_diameter"] = new List<string> { "0.6" };

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                ProcessFrom(PrusaMk4SProcessProfile, "0.10mm FAST DETAIL @MK4S 0.4"),
                machine);

        resolution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.ConditionNotSatisfied);
        resolution.Settings.GetValueOrDefault("compatible_printers").As<List<string>>()
            .Should().NotContain(
                "Prusa MK4S 0.6 nozzle",
                "injecting here would force an incompatible pairing past the gate");
    }

    [Fact(DisplayName = "A profile that names a different printer entirely is left untouched")]
    public void ResolveProcessCompatiblePrinters_WrongFamily_LeavesTheGateUnsatisfied()
    {
        MachineProfileDto machine = MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle");
        var process = new ProcessProfileDto
        {
            Name = "0.10mm @MK3S 0.4",
            Settings = new Dictionary<string, object>
            {
                ["compatible_printers"] = new List<string>(),
                ["compatible_printers_condition"] = "printer_notes=~/.*PRINTER_MODEL_MK3.*/ and nozzle_diameter[0]==0.4",
            },
        };

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(process, machine);

        resolution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.ConditionNotSatisfied);
        resolution.Settings["compatible_printers"].As<List<string>>().Should().BeEmpty();
    }

    [Fact(DisplayName = "A profile that already declares compatible printers keeps exactly what it declared")]
    public void ResolveProcessCompatiblePrinters_ExplicitArray_IsNotRewritten()
    {
        MachineProfileDto machine = MachineFrom(SampleMachineProfile, "Phrozen Arco 0.4 nozzle");
        machine.Settings!["from"] = "system";

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                ProcessFrom(SampleProcessProfile, "0.20mm Standard @Phrozen Arco 0.4 nozzle"),
                machine);

        resolution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.AlreadyDeclared);
        resolution.Settings["compatible_printers"].Should().BeOfType<List<string>>()
            .Which.Should().ContainSingle().Which.Should().Be("Phrozen Arco 0.4 nozzle");
    }

    [Fact(DisplayName = "A profile that declares an explicit array naming a DIFFERENT machine is still not rewritten")]
    public void ResolveProcessCompatiblePrinters_ExplicitArrayForAnotherMachine_IsNotRewritten()
    {
        MachineProfileDto machine = MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle");

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                ProcessFrom(SampleProcessProfile, "0.20mm Standard @Phrozen Arco 0.4 nozzle"),
                machine);

        // Rewriting here would silently repair a mismatch OrcaSlicer is entitled to reject.
        resolution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.AlreadyDeclared);
        resolution.Settings["compatible_printers"].Should().BeOfType<List<string>>()
            .Which.Should().NotContain("Prusa MK4S 0.4 nozzle");
    }

    [Fact(DisplayName = "A profile constraining no printer at all is treated as universally available")]
    public void ResolveProcessCompatiblePrinters_NoArrayAndNoCondition_IsMaterialized()
    {
        var process = new ProcessProfileDto
        {
            Name = "universal",
            Settings = new Dictionary<string, object> { ["layer_height"] = "0.2" },
        };

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                process,
                MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle"));

        resolution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.InjectedUnconditional);
        resolution.Settings["compatible_printers"].Should().BeOfType<List<string>>()
            .Which.Should().ContainSingle().Which.Should().Be("Prusa MK4S 0.4 nozzle");
    }

    [Fact(DisplayName = "An empty compatible_printers array counts as no declaration, which is exactly the reported failure")]
    public void ResolveProcessCompatiblePrinters_EmptyArray_IsTreatedAsUndeclared()
    {
        ProcessProfileDto process = ProcessFrom(SampleProcessProfile, "0.20mm Standard @Phrozen Arco 0.4 nozzle");
        process.Settings["compatible_printers"] = new List<string>();

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                process,
                MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle"));

        resolution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.InjectedUnconditional);
        resolution.Settings["compatible_printers"].Should().BeOfType<List<string>>()
            .Which.Should().ContainSingle().Which.Should().Be("Prusa MK4S 0.4 nozzle");
    }

    [Fact(DisplayName = "Reconciling the process document does not mutate the shared cached profile")]
    public void ResolveProcessCompatiblePrinters_DoesNotMutateSourceOrDropKeys()
    {
        ProcessProfileDto process = ProcessFrom(PrusaMk4SProcessProfile, "0.10mm FAST DETAIL @MK4S 0.4");
        int originalCount = process.Settings.Count;

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                process,
                MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle"));

        // Profiles come from a shared cache, so the emitted document must not edit them.
        process.Settings["compatible_printers"].As<List<string>>().Should().BeEmpty();
        process.Settings.Should().HaveCount(originalCount);
        resolution.Settings.Should().HaveCount(originalCount);
        resolution.Settings["compatible_printers"].As<List<string>>()
            .Should().ContainSingle().Which.Should().Be("Prusa MK4S 0.4 nozzle");
        resolution.Settings["layer_height"].Should().Be("0.1");
        resolution.Settings["name"].Should().Be("0.10mm FAST DETAIL @MK4S 0.4");
    }

    [Fact(DisplayName = "The condition is read from the DTO when the settings bag does not carry it")]
    public void ResolveProcessCompatiblePrinters_ConditionOnlyOnDto_IsStillHonoured()
    {
        var process = new ProcessProfileDto
        {
            Name = "dto-only-condition",
            CompatiblePrintersCondition = "printer_notes=~/.*PRINTER_MODEL_MK3.*/",
            Settings = new Dictionary<string, object> { ["layer_height"] = "0.2" },
        };

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                process,
                MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle"));

        // Without the DTO fallback this would be mistaken for an unconstrained profile and
        // materialized, forcing an MK3-only process onto an MK4S.
        resolution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.ConditionNotSatisfied);
        resolution.Settings.Should().NotContainKey("compatible_printers");
    }

    [Fact(DisplayName = "A submission override cannot relax the profile's own compatibility condition")]
    public void ResolveProcessCompatiblePrinters_OverriddenCondition_CannotAuthorizeAPairing()
    {
        // A submission's `overrides` object writes arbitrary keys into the settings bag, so this
        // is what an attempt to authorize an incompatible pairing looks like by the time it
        // reaches the worker: the profile declares an MK3-only condition, the settings bag has
        // been overwritten with one that matches anything.
        var process = new ProcessProfileDto
        {
            Name = "0.10mm @MK3S 0.4",
            CompatiblePrintersCondition = "printer_notes=~/.*PRINTER_MODEL_MK3.*/",
            Settings = new Dictionary<string, object>
            {
                ["compatible_printers"] = new List<string>(),
                ["compatible_printers_condition"] = "name=~/.*/",
            },
        };

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                process,
                MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle"));

        resolution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.ConditionNotSatisfied,
            "the profile's own declared condition must outrank the mutable settings bag");
        resolution.Settings["compatible_printers"].As<List<string>>().Should().BeEmpty();
    }

    [Fact(DisplayName = "Deleting the condition from the settings bag cannot turn a constrained profile into a universal one")]
    public void ResolveProcessCompatiblePrinters_ConditionRemovedFromSettings_StillEnforced()
    {
        var process = new ProcessProfileDto
        {
            Name = "0.10mm @MK3S 0.4",
            CompatiblePrintersCondition = "printer_notes=~/.*PRINTER_MODEL_MK3.*/",
            Settings = new Dictionary<string, object>
            {
                ["compatible_printers"] = new List<string>(),
                ["compatible_printers_condition"] = string.Empty,
            },
        };

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                process,
                MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle"));

        resolution.Outcome.Should().NotBe(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.InjectedUnconditional,
            "blanking the bag's condition must not be read as 'this profile constrains nothing'");
        resolution.Settings["compatible_printers"].As<List<string>>().Should().BeEmpty();
    }

    [Theory(DisplayName = "The machine's system preset name mirrors OrcaSlicer's own derivation from `from`")]
    [InlineData("system", "Prusa MK4S 0.4 nozzle")]
    [InlineData("User", "fdm_machine_common_mk4s")]
    [InlineData("user", "fdm_machine_common_mk4s")]
    public void ResolveMachineSystemPresetName_FollowsTheFromKey(string from, string expected)
    {
        using JsonDocument doc = JsonDocument.Parse(PrusaMk4SMachineProfile);
        Dictionary<string, object> settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement);
        settings["from"] = from;

        OrcaSlicingPipelineService.ResolveMachineSystemPresetName(settings, "ignored fallback")
            .Should().Be(expected);
    }

    [Fact(DisplayName = "A machine document with nothing to derive from falls back to the resolved profile name")]
    public void ResolveMachineSystemPresetName_NothingDerivable_UsesTheFallback()
    {
        var settings = new Dictionary<string, object> { ["from"] = "system" };

        OrcaSlicingPipelineService.ResolveMachineSystemPresetName(settings, "Prusa MK4S 0.4 nozzle")
            .Should().Be("Prusa MK4S 0.4 nozzle");
        OrcaSlicingPipelineService.ResolveMachineSystemPresetName(settings, null)
            .Should().BeNull();
    }

    [Fact(DisplayName = "The pre-flight check names which document cannot satisfy the gate")]
    public void DescribeUnsatisfiableCompatibilityGate_ReportsTheFailingDocument()
    {
        string machineJson = OrcaSlicingPipelineService.SettingsDictToNativeJson(
            MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle").Settings);

        string undeclaredProcessJson = OrcaSlicingPipelineService.SettingsDictToNativeJson(
            ProcessFrom(PrusaMk4SProcessProfile, "0.10mm FAST DETAIL @MK4S 0.4").Settings);
        OrcaSlicingPipelineService.DescribeUnsatisfiableCompatibilityGate(machineJson, undeclaredProcessJson)
            .Should().Contain("declares no compatible printers");

        string mismatchedProcessJson = OrcaSlicingPipelineService.SettingsDictToNativeJson(
            ProcessFrom(SampleProcessProfile, "0.20mm Standard @Phrozen Arco 0.4 nozzle").Settings);
        OrcaSlicingPipelineService.DescribeUnsatisfiableCompatibilityGate(machineJson, mismatchedProcessJson)
            .Should().Contain("do not include the machine's system preset 'Prusa MK4S 0.4 nozzle'");
    }

    [Fact(DisplayName = "The pre-flight check never reports a parsing problem as the cause of a failure")]
    public void DescribeUnsatisfiableCompatibilityGate_UnreadableDocuments_ReportNothing()
    {
        // Malformed JSON.
        OrcaSlicingPipelineService.DescribeUnsatisfiableCompatibilityGate("not json", "{}")
            .Should().BeNull();

        // A document that is not an object at all — JsonElement's property accessors throw
        // InvalidOperationException, not JsonException, on these.
        OrcaSlicingPipelineService.DescribeUnsatisfiableCompatibilityGate("[]", "{}")
            .Should().BeNull();
        OrcaSlicingPipelineService.DescribeUnsatisfiableCompatibilityGate("{}", "[]")
            .Should().BeNull();

        // A compatible_printers array holding non-string entries. This reaches the worker on the
        // verbatim native-profile path, where documents are digest-verified but not shape-verified.
        const string machine = """{"type":"machine","name":"M","from":"system"}""";
        const string process = """{"type":"process","compatible_printers":[0.4,{"a":1},["b"]]}""";
        Action act = () =>
            OrcaSlicingPipelineService.DescribeUnsatisfiableCompatibilityGate(machine, process);
        act.Should().NotThrow("a diagnostic must never become the reported cause of a job failure");
        OrcaSlicingPipelineService.DescribeUnsatisfiableCompatibilityGate(machine, process)
            .Should().Contain("do not include the machine's system preset 'M'");
    }

    // ── issue #1795 × #1768: the injected name must match the document actually emitted ────────
    //
    // #1768 rewrites the emitted machine document's `inherits` to the profile's name. OrcaSlicer
    // reads the system preset name from `inherits` whenever `from` is not "system", so for a
    // `from`: "User" machine the value it compares against is the REWRITTEN one. Deriving the
    // injected entry from the cached profile settings instead would name the vendor bundle's
    // internal base, and the pairing would still be rejected with -17 despite both fixes.

    [Fact(DisplayName = "For a user preset the injected entry follows the emitted (rewritten) inherits, not the cached vendor base")]
    public void ResolveProcessCompatiblePrinters_UserPreset_FollowsTheEmittedMachineDocument()
    {
        MachineProfileDto machine = MachineFrom(PrusaMk4SMachineProfile, "Prusa MK4S 0.4 nozzle");
        machine.Settings!["from"] = "User";
        machine.Settings["inherits"] = "fdm_machine_common_mk4s";

        // Exactly what GenerateProfileJsonFilesAsync writes to machine.json.
        Dictionary<string, object> emitted = OrcaSlicingPipelineService.WithSystemPresetInherits(
            machine.Settings, machine.Name);
        emitted["inherits"].Should().Be("Prusa MK4S 0.4 nozzle", "issue #1768 rewrites this key");

        OrcaSlicingPipelineService.ProcessCompatibilityResolution resolution =
            OrcaSlicingPipelineService.ResolveProcessCompatiblePrinters(
                ProcessFrom(PrusaMk4SProcessProfile, "0.10mm FAST DETAIL @MK4S 0.4"),
                machine,
                emitted);

        resolution.Outcome.Should().Be(
            OrcaSlicingPipelineService.ProcessCompatibilityOutcome.InjectedFromCondition);
        resolution.Settings["compatible_printers"].As<List<string>>()
            .Should().ContainSingle().Which.Should().Be(
                "Prusa MK4S 0.4 nozzle",
                "OrcaSlicer reads the rewritten inherits for a from:\"User\" preset");
        resolution.Settings["compatible_printers"].As<List<string>>()
            .Should().NotContain(
                "fdm_machine_common_mk4s",
                "deriving from the cached settings would name the vendor base and still fail the gate");

        // The full invariant, over the two documents as they will actually be written.
        OrcaSlicingPipelineService.DescribeUnsatisfiableCompatibilityGate(
            OrcaSlicingPipelineService.SettingsDictToNativeJson(emitted),
            OrcaSlicingPipelineService.SettingsDictToNativeJson(resolution.Settings))
            .Should().BeNull();
    }

    [Fact(DisplayName = "Serializing a profile never mutates the shared cached settings bag")]
    public void SettingsDictToNativeJson_DoesNotMutateTheCallersSettings()
    {
        using JsonDocument doc = JsonDocument.Parse(SampleMachineProfile);
        Dictionary<string, object> settings = OrcaProfilesService.SerializeElementToDict(doc.RootElement);
        int originalCount = settings.Count;

        string json = OrcaSlicingPipelineService.SettingsDictToNativeJson(settings);

        // SanitizeForCli injects CLI-only defaults. Those belong in the emitted document, never in
        // the shared cache: profiles are reused across concurrently-prepared jobs, so writing to
        // them here is both a leak between jobs and an unsynchronized write to a Dictionary another
        // thread may be reading.
        settings.Should().HaveCount(originalCount);
        settings.Should().NotContainKey("extruder_type");
        settings.Should().NotContainKey("nozzle_volume_type");
        json.Should().Contain("\"extruder_type\"", "the emitted document still carries the defaults");
    }
}
