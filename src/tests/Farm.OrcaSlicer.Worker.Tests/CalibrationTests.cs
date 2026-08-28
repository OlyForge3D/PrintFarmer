using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using Farm.OrcaSlicer.Worker.Services.Calibration;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Tests for the OrcaSlicer worker calibration mode (issue #1938): calibration method wire-name
/// parsing, per-band temperature tower gcode generation, and per-object flow-rate 3MF
/// configuration.
/// </summary>
public class CalibrationTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), $"calibration-test-{Guid.NewGuid():N}");

    public CalibrationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // best effort
        }

        GC.SuppressFinalize(this);
    }

    #region CalibrationMethods

    [Theory]
    [InlineData("flow_rate_pass_1", CalibrationMethod.FlowRatePass1)]
    [InlineData("flow_rate_pass_2", CalibrationMethod.FlowRatePass2)]
    [InlineData("temperature_tower", CalibrationMethod.TemperatureTower)]
    [InlineData("FLOW_RATE_PASS_1", CalibrationMethod.FlowRatePass1)]
    [InlineData("flow_rate_yolo_recommended", CalibrationMethod.FlowRateYoloRecommended)]
    [InlineData("flow_rate_yolo_perfectionist", CalibrationMethod.FlowRateYoloPerfectionist)]
    [InlineData("FLOW_RATE_YOLO_RECOMMENDED", CalibrationMethod.FlowRateYoloRecommended)]
    [InlineData("retraction", CalibrationMethod.Retraction)]
    [InlineData("RETRACTION", CalibrationMethod.Retraction)]
    [InlineData("pressure_advance_tower", CalibrationMethod.PressureAdvanceTower)]
    [InlineData("PRESSURE_ADVANCE_TOWER", CalibrationMethod.PressureAdvanceTower)]
    [InlineData("max_volumetric_speed", CalibrationMethod.MaximumVolumetricSpeed)]
    [InlineData("MAX_VOLUMETRIC_SPEED", CalibrationMethod.MaximumVolumetricSpeed)]
    [InlineData("input_shaping", CalibrationMethod.InputShaping)]
    [InlineData("INPUT_SHAPING", CalibrationMethod.InputShaping)]
    public void TryParse_SupportedWireName_ReturnsExpectedMethod(string wireName, CalibrationMethod expected)
    {
        bool parsed = CalibrationMethods.TryParse(wireName, out CalibrationMethod method);

        parsed.Should().BeTrue();
        method.Should().Be(expected);
    }

    [Theory]
    [InlineData("pa_pattern")]
    [InlineData("pa_line")]
    [InlineData("not_a_real_method")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_UnsupportedOrMissingWireName_ReturnsFalse(string? wireName)
    {
        // PA Pattern (GPL-3.0 provenance) and PA Line (Bambu-specific) are deliberately not
        // supported yet — both retraction (issue #2137) and max_volumetric_speed (issue #2135)
        // are now built and parse successfully, so neither belongs in this list any more; see
        // their dedicated tests below. All remaining entries must fail clearly rather than
        // silently degrading into a generic slice failure.
        bool parsed = CalibrationMethods.TryParse(wireName, out _);

        parsed.Should().BeFalse();
    }

    [Fact]
    public void ToWireName_RoundTripsWithTryParse()
    {
        foreach (string wireName in CalibrationMethods.SupportedWireNames)
        {
            CalibrationMethods.TryParse(wireName, out CalibrationMethod method).Should().BeTrue();
            CalibrationMethods.ToWireName(method).Should().Be(wireName);
        }
    }

    [Fact]
    public void ClientAcceptedWireNames_IncludesBothYoloMethods()
    {
        // Issue #2141/#2142: both FlowRateYoloRecommended and FlowRateYoloPerfectionist are now
        // slicer-supported and must appear in the list a controller advertises to clients as
        // "supported methods" — neither is gated any longer.
        CalibrationMethods.ClientAcceptedWireNames.Should()
            .Contain("flow_rate_yolo_recommended")
            .And.Contain("flow_rate_yolo_perfectionist")
            .And.Contain("retraction", "issue #2137 makes retraction a fully slicer-supported method");

        // Issue #2135: max volumetric speed is now fully slicer-supported, so it must be
        // advertised to clients like every other built method.
        CalibrationMethods.ClientAcceptedWireNames.Should().Contain("max_volumetric_speed");

        // Issue #2139: input shaping is report-only but fully slicer-supported (the worker
        // slices the bundled ringing tower unmodified), so it must be advertised too.
        CalibrationMethods.ClientAcceptedWireNames.Should().Contain("input_shaping");

        foreach (string wireName in CalibrationMethods.ClientAcceptedWireNames)
        {
            CalibrationMethods.TryParse(wireName, out CalibrationMethod method).Should().BeTrue();
            CalibrationMethods.IsSlicerSupported(method).Should().BeTrue();
        }

        CalibrationMethods.ClientAcceptedWireNames.Should()
            .HaveCount(CalibrationMethods.SupportedWireNames.Count);
    }

    [Theory]
    [InlineData(CalibrationMethod.FlowRateYoloRecommended, "Orca-LinearFlow.3mf")]
    [InlineData(CalibrationMethod.FlowRateYoloPerfectionist, "Orca-LinearFlow_fine.3mf")]
    [InlineData(CalibrationMethod.Retraction, "retraction_tower.drc")]
    public void DefaultModelFileName_YoloMethods_ReturnsExpectedFileName(CalibrationMethod method, string expected)
    {
        CalibrationMethods.DefaultModelFileName(method).Should().Be(expected);
    }

    [Theory]
    [InlineData(CalibrationMethod.FlowRateYoloRecommended, "Orca-LinearFlow.3mf")]
    [InlineData(CalibrationMethod.FlowRateYoloPerfectionist, "Orca-LinearFlow_fine.3mf")]
    public void RelativeResourcePath_YoloMethods_ResolvesUnderFilamentFlowDirectory(CalibrationMethod method, string expectedFileName)
    {
        CalibrationMethods.RelativeResourcePath(method).Should().Be(Path.Combine("filament_flow", expectedFileName));
    }

    [Fact]
    public void RelativeResourcePath_Retraction_ResolvesUnderRetractionDirectory()
    {
        CalibrationMethods.RelativeResourcePath(CalibrationMethod.Retraction)
            .Should().Be(Path.Combine("retraction", "retraction_tower.drc"));
    }

    [Fact]
    public void DefaultModelFileName_PressureAdvanceTower_ReturnsUpstreamResourceFileName()
    {
        // "tower_with_seam.drc" matches OrcaSlicer upstream's resources/calib/pressure_advance/
        // directory (issue #2136), mirroring temperature_tower.drc for the temperature tower.
        CalibrationMethods.DefaultModelFileName(CalibrationMethod.PressureAdvanceTower).Should().Be("tower_with_seam.drc");
    }

    [Fact]
    public void RelativeResourcePath_PressureAdvanceTower_ResolvesUnderPressureAdvanceDirectory()
    {
        CalibrationMethods.RelativeResourcePath(CalibrationMethod.PressureAdvanceTower).Should()
            .Be(Path.Combine("pressure_advance", "tower_with_seam.drc"));
    }

    [Fact]
    public void IsSlicerSupported_PressureAdvanceTower_ReturnsTrue()
    {
        // Issue #2136: pressure advance tower is fully slicer-supported —
        // OrcaSlicingPipelineService.ApplyPressureAdvanceTowerGcodeAsync implements it.
        // (Both YOLO flow-calibration methods are also slicer-supported as of issues
        // #2141/#2142 — see the dedicated ClientAcceptedWireNames tests below.)
        CalibrationMethods.IsSlicerSupported(CalibrationMethod.PressureAdvanceTower).Should().BeTrue();
    }

    [Fact]
    public void DefaultModelFileName_MaxVolumetricSpeed_ReturnsSpeedTestStructureDrc()
    {
        // Verified against a local OrcaSlicer install: resources/calib/volumetric_speed/SpeedTestStructure.drc.
        CalibrationMethods.DefaultModelFileName(CalibrationMethod.MaximumVolumetricSpeed).Should().Be("SpeedTestStructure.drc");
    }

    [Fact]
    public void RelativeResourcePath_MaxVolumetricSpeed_ResolvesUnderVolumetricSpeedDirectory()
    {
        CalibrationMethods.RelativeResourcePath(CalibrationMethod.MaximumVolumetricSpeed).Should()
            .Be(Path.Combine("volumetric_speed", "SpeedTestStructure.drc"));
    }

    [Fact]
    public void IsSlicerSupported_MaxVolumetricSpeed_ReturnsTrue()
    {
        CalibrationMethods.IsSlicerSupported(CalibrationMethod.MaximumVolumetricSpeed).Should().BeTrue();
    }

    [Fact]
    public void DefaultModelFileName_InputShaping_ReturnsRingingTowerDrc()
    {
        // Verified against a local OrcaSlicer install: resources/calib/input_shaping/ringing_tower.drc.
        CalibrationMethods.DefaultModelFileName(CalibrationMethod.InputShaping).Should().Be("ringing_tower.drc");
    }

    [Fact]
    public void RelativeResourcePath_InputShaping_ResolvesUnderInputShapingDirectory()
    {
        CalibrationMethods.RelativeResourcePath(CalibrationMethod.InputShaping).Should()
            .Be(Path.Combine("input_shaping", "ringing_tower.drc"));
    }

    [Fact]
    public void IsSlicerSupported_InputShaping_ReturnsTrue()
    {
        // Report-only (issue #2139) does not mean unsupported: the worker can and does slice the
        // bundled ringing tower unmodified, exactly like TemperatureTower/MaximumVolumetricSpeed.
        CalibrationMethods.IsSlicerSupported(CalibrationMethod.InputShaping).Should().BeTrue();
    }

    [Theory]
    [InlineData("cornering", CalibrationMethod.Cornering)]
    [InlineData("CORNERING", CalibrationMethod.Cornering)]
    public void TryParse_Cornering_ReturnsCorneringMethod(string wireName, CalibrationMethod expected)
    {
        bool parsed = CalibrationMethods.TryParse(wireName, out CalibrationMethod method);

        parsed.Should().BeTrue();
        method.Should().Be(expected);
    }

    [Fact]
    public void DefaultModelFileName_Cornering_ReturnsScvV2Drc()
    {
        // Verified against a local OrcaSlicer install: resources/calib/cornering/SCV-V2.drc.
        CalibrationMethods.DefaultModelFileName(CalibrationMethod.Cornering).Should().Be("SCV-V2.drc");
    }

    [Fact]
    public void RelativeResourcePath_Cornering_ResolvesUnderCorneringDirectory()
    {
        CalibrationMethods.RelativeResourcePath(CalibrationMethod.Cornering).Should()
            .Be(Path.Combine("cornering", "SCV-V2.drc"));
    }

    [Fact]
    public void IsSlicerSupported_Cornering_ReturnsTrue()
    {
        // Cornering is a built, catalogued method (issue #2138) — its resource is copied via the
        // generic fallback path in PrepareCalibrationModel like TemperatureTower/MaxVolumetricSpeed.
        CalibrationMethods.IsSlicerSupported(CalibrationMethod.Cornering).Should().BeTrue();
    }

    [Fact]
    public void ClientAcceptedWireNames_IncludesCornering()
    {
        CalibrationMethods.ClientAcceptedWireNames.Should().Contain("cornering");
    }

    #endregion

    #region InputShapingFirmwareFlavors

    [Theory]
    [InlineData("klipper")]
    [InlineData("KLIPPER")]
    [InlineData("Klipper")]
    [InlineData("marlin")]
    [InlineData("MARLIN")]
    [InlineData(" klipper ")]
    public void IsSupported_KnownFirmwareFlavorAnyCase_ReturnsTrue(string firmwareFlavor)
    {
        InputShapingFirmwareFlavors.IsSupported(firmwareFlavor).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("reprap")]
    [InlineData("smoothieware")]
    [InlineData("klippper")]
    public void IsSupported_UnsupportedOrMissingFirmwareFlavor_ReturnsFalse(string? firmwareFlavor)
    {
        InputShapingFirmwareFlavors.IsSupported(firmwareFlavor).Should().BeFalse();
    }

    #endregion

    #region CalibrationParameters — FirmwareFlavor

    [Fact]
    public void CalibrationParameters_Parse_InputShapingWithFirmwareFlavor_ParsesFlavor()
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(
            """{"firmware_flavor": "klipper"}""",
            CalibrationMethod.InputShaping);

        parameters.FirmwareFlavor.Should().Be("klipper");
    }

    [Fact]
    public void CalibrationParameters_Parse_InputShapingWithoutParams_FirmwareFlavorIsNull()
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(null, CalibrationMethod.InputShaping);

        parameters.FirmwareFlavor.Should().BeNull();
    }

    [Fact]
    public void CalibrationParameters_Parse_InputShapingWithNonStringFlavor_FallsBackToNull()
    {
        // A malformed client payload (firmware_flavor as a number) must not throw — it simply
        // fails to populate FirmwareFlavor, which the worker then refuses explicitly.
        CalibrationParameters parameters = CalibrationParameters.Parse(
            """{"firmware_flavor": 42}""",
            CalibrationMethod.InputShaping);

        parameters.FirmwareFlavor.Should().BeNull();
    }

    [Fact]
    public void CalibrationParameters_Parse_TemperatureTowerWithExtraneousFirmwareFlavorKey_NumericFieldsStillParseCorrectly()
    {
        // Regression coverage for the Parse rewrite (issue #2139): a payload combining an
        // unrelated method's numeric field with a string firmware_flavor key must not regress the
        // numeric method's own parsing. Under the old whole-payload Dictionary<string,double>
        // deserialization this JSON would have thrown on the string value and silently fallen back
        // to *all* TemperatureTower defaults, hiding the client-supplied start_temperature entirely.
        CalibrationParameters parameters = CalibrationParameters.Parse(
            """{"start_temperature": 220, "firmware_flavor": "marlin"}""",
            CalibrationMethod.TemperatureTower);

        parameters.StartTemperatureC.Should().Be(
            220,
            "the per-key JsonDocument-based parse must extract start_temperature correctly even " +
            "when an unrelated string key is present elsewhere in the same JSON object");
    }

    [Fact]
    public void CalibrationParameters_Parse_InputShapingIgnoresUnrelatedNumericKeys()
    {
        // InputShaping's Parse case only reads firmware_flavor — it has no numeric parameters of
        // its own (report-only, issue #2139) — so a stray numeric key from another method's
        // vocabulary must not cause a throw or otherwise interfere with FirmwareFlavor parsing.
        CalibrationParameters parameters = CalibrationParameters.Parse(
            """{"start_temperature": 220, "firmware_flavor": "marlin"}""",
            CalibrationMethod.InputShaping);

        parameters.FirmwareFlavor.Should().Be("marlin");
    }

    #endregion

    #region CorneringFirmwareFlavorGate

    [Theory]
    [InlineData("marlin")]
    [InlineData("Marlin")]
    [InlineData("marlin2")]
    [InlineData("MARLIN2")]
    [InlineData("klipper")]
    [InlineData("Klipper")]
    public async Task ValidateCorneringFirmwareFlavorAsync_SupportedFlavor_DoesNotThrow(string gcodeFlavor)
    {
        string machineJsonPath = WriteMachineJsonWithGcodeFlavor(gcodeFlavor);
        DistributedSlicingJob job = new();

        Func<Task> act = () => OrcaSlicingPipelineService.ValidateCorneringFirmwareFlavorAsync(
            job, machineJsonPath, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("reprapfirmware")]
    [InlineData("smoothieware")]
    [InlineData("sprinter")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ValidateCorneringFirmwareFlavorAsync_UnsupportedOrMissingFlavor_ThrowsExplicitly(string? gcodeFlavor)
    {
        // Per Dallas's architecture decision on #2138: an unsupported or missing firmware
        // flavor must be refused explicitly (InvalidOperationException), never a silent no-op.
        string machineJsonPath = gcodeFlavor is null
            ? WriteMachineJsonWithoutGcodeFlavor()
            : WriteMachineJsonWithGcodeFlavor(gcodeFlavor);
        DistributedSlicingJob job = new();

        Func<Task> act = () => OrcaSlicingPipelineService.ValidateCorneringFirmwareFlavorAsync(
            job, machineJsonPath, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{job.Id}*");
    }

    [Fact]
    public async Task ValidateCorneringFirmwareFlavorAsync_OversizedUnsupportedFlavor_TruncatesValueInMessage()
    {
        // Mirrors the pressure advance tower gate's truncation safeguard: an adversarial or
        // malformed machine profile must not be able to smuggle an arbitrarily large gcode_flavor
        // value into job failure telemetry/logs via this exception message.
        string oversizedFlavor = new('x', 500);
        string machineJsonPath = WriteMachineJsonWithGcodeFlavor(oversizedFlavor);
        DistributedSlicingJob job = new();

        Func<Task> act = () => OrcaSlicingPipelineService.ValidateCorneringFirmwareFlavorAsync(
            job, machineJsonPath, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().NotContain(oversizedFlavor)
            .And.Contain(new string('x', 64));
    }

    [Fact]
    public async Task ValidateCorneringFirmwareFlavorAsync_SingleElementArrayEncoding_IsTolerated()
    {
        // OrcaSlicer sometimes encodes scalar config values as single-element JSON arrays;
        // OrcaRawValueParser.ParseStringValue already tolerates this for every other raw value,
        // and the cornering gate reuses it for gcode_flavor.
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        File.WriteAllText(machineJsonPath, """{"gcode_flavor": ["klipper"]}""");
        DistributedSlicingJob job = new();

        Func<Task> act = () => OrcaSlicingPipelineService.ValidateCorneringFirmwareFlavorAsync(
            job, machineJsonPath, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private string WriteMachineJsonWithGcodeFlavor(string gcodeFlavor)
    {
        string path = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        string json = JsonSerializer.Serialize(new Dictionary<string, string> { ["gcode_flavor"] = gcodeFlavor });
        File.WriteAllText(path, json);
        return path;
    }

    private string WriteMachineJsonWithoutGcodeFlavor()
    {
        string path = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"printer_model": "generic"}""");
        return path;
    }

    #endregion

    #region PressureAdvanceTowerGcodeBuilder

    [Theory]
    [InlineData("klipper", CalibrationFirmwareFlavor.Klipper)]
    [InlineData("KLIPPER", CalibrationFirmwareFlavor.Klipper)]
    [InlineData("marlin", CalibrationFirmwareFlavor.Marlin)]
    [InlineData("marlin2", CalibrationFirmwareFlavor.Marlin)]
    [InlineData("Marlin2", CalibrationFirmwareFlavor.Marlin)]
    public void TryResolveFirmwareFlavor_SupportedFlavor_ReturnsExpected(string gcodeFlavor, CalibrationFirmwareFlavor expected)
    {
        PressureAdvanceTowerGcodeBuilder.TryResolveFirmwareFlavor(gcodeFlavor).Should().Be(expected);
    }

    [Theory]
    [InlineData("reprap")]
    [InlineData("reprapfirmware")]
    [InlineData("repetier")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void TryResolveFirmwareFlavor_UnsupportedOrMissingFlavor_ReturnsNull(string? gcodeFlavor)
    {
        // The pressure advance tower method must refuse any firmware it cannot emit a correct
        // command for, rather than guessing — see PressureAdvanceTowerGcodeBuilder's remarks.
        PressureAdvanceTowerGcodeBuilder.TryResolveFirmwareFlavor(gcodeFlavor).Should().BeNull();
    }

    [Fact]
    public void ReadGcodeFlavor_ValidMachineProfile_ReturnsFlavor()
    {
        string machineProfileJson = """{"name": "Test Machine", "gcode_flavor": "klipper"}""";

        PressureAdvanceTowerGcodeBuilder.ReadGcodeFlavor(machineProfileJson).Should().Be("klipper");
    }

    [Theory]
    [InlineData("""{"name": "Test Machine"}""")] // missing gcode_flavor
    [InlineData("not json")]
    [InlineData("")]
    [InlineData(null)]
    public void ReadGcodeFlavor_MissingOrMalformedProfile_ReturnsNull(string? machineProfileJson)
    {
        PressureAdvanceTowerGcodeBuilder.ReadGcodeFlavor(machineProfileJson).Should().BeNull();
    }

    [Fact]
    public void BuildLayerChangeGcode_Klipper_EmitsSetPressureAdvancePerBand()
    {
        string gcode = PressureAdvanceTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Klipper,
            startAdvance: 0.0,
            advanceStep: 0.01,
            bandHeightMm: 5,
            bandCount: 4);

        gcode.Should().Contain("{if layer_z >= 15}SET_PRESSURE_ADVANCE ADVANCE=0.03");
        gcode.Should().Contain("{elsif layer_z >= 10}SET_PRESSURE_ADVANCE ADVANCE=0.02");
        gcode.Should().Contain("{elsif layer_z >= 5}SET_PRESSURE_ADVANCE ADVANCE=0.01");
        gcode.Should().Contain("{else}SET_PRESSURE_ADVANCE ADVANCE=0");
        gcode.Should().NotContain("M900", "Klipper must never receive Marlin's linear-advance command");
    }

    [Fact]
    public void BuildLayerChangeGcode_Marlin_EmitsM900KPerBand()
    {
        string gcode = PressureAdvanceTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Marlin,
            startAdvance: 0.0,
            advanceStep: 0.01,
            bandHeightMm: 5,
            bandCount: 4);

        gcode.Should().Contain("{if layer_z >= 15}M900 K0.03");
        gcode.Should().Contain("{elsif layer_z >= 10}M900 K0.02");
        gcode.Should().Contain("{elsif layer_z >= 5}M900 K0.01");
        gcode.Should().Contain("{else}M900 K0");
        gcode.Should().NotContain("SET_PRESSURE_ADVANCE", "Marlin must never receive Klipper's SET_PRESSURE_ADVANCE macro");
    }

    [Fact]
    public void BuildLayerChangeGcode_TallestBandCheckedFirst()
    {
        string gcode = PressureAdvanceTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Klipper, 0.0, 0.01, bandHeightMm: 5, bandCount: 4);

        gcode.IndexOf("layer_z >= 15", StringComparison.Ordinal)
            .Should().BeLessThan(gcode.IndexOf("layer_z >= 5", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildLayerChangeGcode_InvalidBandCount_ThrowsForPressureAdvanceTower()
    {
        Action act = () => PressureAdvanceTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Klipper, 0.0, 0.01, bandHeightMm: 5, bandCount: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildLayerChangeGcode_InvalidBandHeight_Throws()
    {
        Action act = () => PressureAdvanceTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Klipper, 0.0, 0.01, bandHeightMm: 0, bandCount: 4);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(CalibrationFirmwareFlavor.Klipper, "SET_PRESSURE_ADVANCE ADVANCE=0.02")]
    [InlineData(CalibrationFirmwareFlavor.Marlin, "M900 K0.02")]
    public void BuildLayerChangeGcode_SingleBand_EmitsBareCommandWithoutMalformedElseEndif(
        CalibrationFirmwareFlavor flavor, string expectedCommand)
    {
        // Regression: a single band has no threshold to branch on. Emitting the usual
        // "{else}...{endif}" wrapper with no preceding "{if}" would be a malformed OrcaSlicer
        // custom-gcode template that the slicer's parser rejects; a lone band must instead emit
        // the bare command with no conditional wrapper at all.
        string gcode = PressureAdvanceTowerGcodeBuilder.BuildLayerChangeGcode(
            flavor, startAdvance: 0.02, advanceStep: 0.01, bandHeightMm: 5, bandCount: 1);

        gcode.Should().Be(expectedCommand + "\n");
        gcode.Should().NotContain("{if").And.NotContain("{else}").And.NotContain("{endif}");
    }

    #endregion

    #region TemperatureTowerGcodeBuilder

    [Fact]
    public void BuildLayerChangeGcode_ProducesDescendingTemperaturePerBand()
    {
        string gcode = TemperatureTowerGcodeBuilder.BuildLayerChangeGcode(
            startTemperatureC: 230,
            temperatureStepC: 5,
            bandHeightMm: 10,
            bandCount: 9);

        // 9 bands starting at 230C, stepping down 5C every 10mm: the bottom band (band 0, no
        // threshold) stays at 230C, and the top band (band 8, z >= 80) drops to 190C.
        gcode.Should().StartWith("M104 S");
        gcode.Should().Contain("{if layer_z >= 80}190");
        gcode.Should().Contain("{elsif layer_z >= 70}195");
        gcode.Should().Contain("{elsif layer_z >= 10}225");
        gcode.Should().Contain("{else}230{endif}");

        // The tallest band's condition must be evaluated first, since {if}/{elsif} stops at the
        // first true branch.
        gcode.IndexOf("layer_z >= 80", StringComparison.Ordinal)
            .Should().BeLessThan(gcode.IndexOf("layer_z >= 10", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildLayerChangeGcode_NineBands_HasNineDistinctTemperatures()
    {
        string gcode = TemperatureTowerGcodeBuilder.BuildLayerChangeGcode(
            startTemperatureC: 230,
            temperatureStepC: 5,
            bandHeightMm: 10,
            bandCount: 9);

        IEnumerable<double> temperatures = Enumerable.Range(0, 9).Select(band => 230 - (band * 5.0));
        temperatures.Distinct().Should().HaveCount(9);
        foreach (double temperature in temperatures)
        {
            gcode.Should().Contain(temperature.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void BuildLayerChangeGcode_NineBands_EachThresholdMapsToTheCorrectTemperature()
    {
        // The weaker "value appears somewhere" assertion above cannot catch a swapped band
        // mapping (e.g. band 3's threshold paired with band 6's temperature) -- the acceptance
        // criterion is that the temperature tower "emits the correct temperature change per
        // band", not merely that all nine temperatures appear. This parses the emitted
        // {if}/{elsif}/{else} chain in order and asserts each (threshold, temperature) pair
        // matches the expected band exactly, plus the fallback {else} temperature.
        string gcode = TemperatureTowerGcodeBuilder.BuildLayerChangeGcode(
            startTemperatureC: 230,
            temperatureStepC: 5,
            bandHeightMm: 10,
            bandCount: 9);

        var conditionalPairs = System.Text.RegularExpressions.Regex
            .Matches(gcode, @"\{(?:if|elsif) layer_z >= (?<threshold>[\d.]+)\}(?<temperature>[\d.]+)")
            .Select(m => (
                Threshold: double.Parse(m.Groups["threshold"].Value, System.Globalization.CultureInfo.InvariantCulture),
                Temperature: double.Parse(m.Groups["temperature"].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

        // Bands 8 down to 1, tallest (highest threshold) first: band N sits at z >= N*10 and
        // prints at 230 - N*5.
        List<(double Threshold, double Temperature)> expectedPairs =
            Enumerable.Range(1, 8).Reverse()
                .Select(band => ((double)(band * 10), 230.0 - (band * 5)))
                .ToList();
        conditionalPairs.Should().Equal(expectedPairs);

        var elseMatch = System.Text.RegularExpressions.Regex.Match(gcode, @"\{else\}(?<temperature>[\d.]+)\{endif\}");
        elseMatch.Success.Should().BeTrue();
        double.Parse(elseMatch.Groups["temperature"].Value, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(230); // band 0, no threshold
    }

    [Fact]
    public void BuildLayerChangeGcode_InvalidBandCount_Throws()
    {
        Action act = () => TemperatureTowerGcodeBuilder.BuildLayerChangeGcode(230, 5, 10, bandCount: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region RetractionTowerGcodeBuilder

    [Fact]
    public void BuildLayerChangeGcode_Retraction_ProducesAscendingRetractionPerBand()
    {
        string gcode = RetractionTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Marlin,
            startRetractionMm: 0.2,
            retractionStepMm: 0.2,
            bandHeightMm: 5,
            bandCount: 8);

        // 8 bands starting at 0.2mm, stepping up 0.2mm every 5mm: the bottom band (band 0, no
        // threshold) stays at 0.2mm, and the top band (band 7, z >= 35) reaches 1.6mm.
        gcode.Should().StartWith("M207 S");
        gcode.Should().Contain("{if layer_z >= 35}1.6");
        gcode.Should().Contain("{elsif layer_z >= 30}1.4");
        gcode.Should().Contain("{elsif layer_z >= 5}0.4");
        gcode.Should().Contain("{else}0.2{endif}");

        // The tallest band's condition must be evaluated first, since {if}/{elsif} stops at the
        // first true branch.
        gcode.IndexOf("layer_z >= 35", StringComparison.Ordinal)
            .Should().BeLessThan(gcode.IndexOf("layer_z >= 5", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildLayerChangeGcode_Retraction_EightBands_EachThresholdMapsToTheCorrectRetraction()
    {
        // Mirrors TemperatureTowerGcodeBuilder's equivalent test: the acceptance criterion is
        // that each band's threshold maps to *that band's* retraction length, not merely that
        // all eight lengths appear somewhere in the emitted gcode.
        string gcode = RetractionTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Marlin,
            startRetractionMm: 0.2,
            retractionStepMm: 0.2,
            bandHeightMm: 5,
            bandCount: 8);

        var conditionalPairs = System.Text.RegularExpressions.Regex
            .Matches(gcode, @"\{(?:if|elsif) layer_z >= (?<threshold>[\d.]+)\}(?<retraction>[\d.]+)")
            .Select(m => (
                Threshold: double.Parse(m.Groups["threshold"].Value, System.Globalization.CultureInfo.InvariantCulture),
                Retraction: double.Parse(m.Groups["retraction"].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

        // Bands 7 down to 1, tallest (highest threshold) first: band N sits at z >= N*5 and
        // retracts 0.2 + N*0.2 mm.
        List<(double Threshold, double Retraction)> expectedPairs =
            Enumerable.Range(1, 7).Reverse()
                .Select(band => ((double)(band * 5), Math.Round(0.2 + (band * 0.2), 4)))
                .ToList();
        conditionalPairs.Should().BeEquivalentTo(expectedPairs, options => options.WithStrictOrdering());

        var elseMatch = System.Text.RegularExpressions.Regex.Match(gcode, @"\{else\}(?<retraction>[\d.]+)\{endif\}");
        elseMatch.Success.Should().BeTrue();
        double.Parse(elseMatch.Groups["retraction"].Value, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(0.2); // band 0, no threshold
    }

    [Fact]
    public void BuildLayerChangeGcode_Retraction_EightBands_HasEightDistinctRetractionLengths()
    {
        string gcode = RetractionTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Marlin,
            startRetractionMm: 0.2,
            retractionStepMm: 0.2,
            bandHeightMm: 5,
            bandCount: 8);

        IEnumerable<double> conditionalLengths = System.Text.RegularExpressions.Regex
            .Matches(gcode, @"\{(?:if|elsif) layer_z >= [\d.]+\}(?<retraction>[\d.]+)")
            .Select(m => double.Parse(m.Groups["retraction"].Value, System.Globalization.CultureInfo.InvariantCulture));
        double elseLength = double.Parse(
            System.Text.RegularExpressions.Regex.Match(gcode, @"\{else\}(?<retraction>[\d.]+)\{endif\}").Groups["retraction"].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        List<double> emittedLengths = conditionalLengths.Append(elseLength).ToList();

        emittedLengths.Should().HaveCount(8, "one retraction length per band, including the else/band-0 fallback");
        emittedLengths.Distinct().Should().HaveCount(8, "each band must have a visibly distinct retraction length in the emitted gcode");
    }

    [Fact]
    public void BuildLayerChangeGcode_Retraction_SingleBand_EmitsUnconditionalM207WithoutOrphanedElse()
    {
        // A one-band tower has no threshold to branch on. Naively falling through the same
        // {if}/{elsif}/{else} cascade used for bandCount > 1 would emit a bare
        // "M207 S{else}0.2{endif}" with no matching {if} — a placeholder-syntax error that
        // OrcaSlicer's gcode processor rejects. The builder must special-case this instead.
        string gcode = RetractionTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Marlin,
            startRetractionMm: 0.3,
            retractionStepMm: 0.2,
            bandHeightMm: 5,
            bandCount: 1);

        gcode.Should().Be("M207 S0.3\n");
        gcode.Should().NotContain("{if").And.NotContain("{elsif").And.NotContain("{else").And.NotContain("{endif");
    }

    [Fact]
    public void BuildLayerChangeGcode_Retraction_Klipper_EmitsSetRetractionInsteadOfM207()
    {
        // Issue #2137 review finding: Klipper does not recognize Marlin's M207/M208 at all, so
        // emitting M207 unconditionally would silently no-op the entire tower on a Klipper
        // machine (the printer ignores the unknown gcode and keeps whatever static retraction
        // length its [firmware_retraction] config already had). Klipper's equivalent runtime
        // command is SET_RETRACTION RETRACT_LENGTH=....
        string gcode = RetractionTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Klipper,
            startRetractionMm: 0.2,
            retractionStepMm: 0.2,
            bandHeightMm: 5,
            bandCount: 8);

        gcode.Should().StartWith("SET_RETRACTION RETRACT_LENGTH=");
        gcode.Should().NotContain("M207");
        gcode.Should().Contain("{if layer_z >= 35}1.6");
        gcode.Should().Contain("{else}0.2{endif}");
    }

    [Fact]
    public void BuildLayerChangeGcode_Retraction_Klipper_SingleBand_EmitsUnconditionalSetRetraction()
    {
        string gcode = RetractionTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Klipper,
            startRetractionMm: 0.3,
            retractionStepMm: 0.2,
            bandHeightMm: 5,
            bandCount: 1);

        gcode.Should().Be("SET_RETRACTION RETRACT_LENGTH=0.3\n");
        gcode.Should().NotContain("{if").And.NotContain("{elsif").And.NotContain("{else").And.NotContain("{endif");
    }

    [Fact]
    public void BuildLayerChangeGcode_Retraction_UnsupportedFlavor_Throws()
    {
        Action act = () => RetractionTowerGcodeBuilder.BuildLayerChangeGcode(
            (CalibrationFirmwareFlavor)(-1),
            startRetractionMm: 0.2,
            retractionStepMm: 0.2,
            bandHeightMm: 5,
            bandCount: 8);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildLayerChangeGcode_Retraction_InvalidBandCount_Throws()
    {
        Action act = () => RetractionTowerGcodeBuilder.BuildLayerChangeGcode(CalibrationFirmwareFlavor.Marlin, 0.2, 0.2, 5, bandCount: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildLayerChangeGcode_Retraction_InvalidBandHeight_Throws()
    {
        Action act = () => RetractionTowerGcodeBuilder.BuildLayerChangeGcode(CalibrationFirmwareFlavor.Marlin, 0.2, 0.2, bandHeightMm: 0, bandCount: 8);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region CalibrationParameters

    [Fact]
    public void CalibrationParameters_Parse_NullJson_ReturnsDefaults()
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(null, CalibrationMethod.TemperatureTower);

        parameters.StartTemperatureC.Should().Be(230);
        parameters.TemperatureStepC.Should().Be(5);
        parameters.BandHeightMm.Should().Be(10);
        parameters.BandCount.Should().Be(9);
    }

    [Fact]
    public void CalibrationParameters_Parse_OverridesProvidedKeysOnly()
    {
        string json = """{"start_temperature": 260, "band_count": 5}""";

        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.TemperatureTower);

        parameters.StartTemperatureC.Should().Be(260);
        parameters.BandCount.Should().Be(5);
        parameters.TemperatureStepC.Should().Be(5); // default preserved
        parameters.BandHeightMm.Should().Be(10); // default preserved
    }

    [Fact]
    public void CalibrationParameters_Parse_MalformedJson_FallsBackToDefaults()
    {
        CalibrationParameters parameters = CalibrationParameters.Parse("not json", CalibrationMethod.TemperatureTower);

        parameters.StartTemperatureC.Should().Be(230);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CalibrationParameters_Parse_BlankJson_RoutesThroughMethodSwitchAndReturnsDefaults(string blankJson)
    {
        // Regression for the Parse refactor (issue #2136): blank/whitespace input must still route
        // through the method switch below, not short-circuit and return the record's raw field
        // defaults directly -- those two things happen to agree for TemperatureTower (whose own
        // field defaults ARE its method defaults), but must not silently diverge for a method whose
        // defaults differ (see the PressureAdvanceTower case in this region).
        CalibrationParameters parameters = CalibrationParameters.Parse(blankJson, CalibrationMethod.TemperatureTower);

        parameters.StartTemperatureC.Should().Be(230);
        parameters.TemperatureStepC.Should().Be(5);
        parameters.BandHeightMm.Should().Be(10);
        parameters.BandCount.Should().Be(9);
    }

    [Theory]
    [InlineData("""{"band_count": 100000}""")] // absurdly large: would blow up gcode-template generation
    [InlineData("""{"band_count": 0}""")] // below the minimum of 1
    [InlineData("""{"band_count": -5}""")]
    public void CalibrationParameters_Parse_OutOfRangeBandCount_FallsBackToDefault(string json)
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.TemperatureTower);

        parameters.BandCount.Should().Be(9, "an adversarial or malformed band_count must never reach the gcode builder");
    }

    [Theory]
    [InlineData("""{"start_temperature": 99999}""")]
    [InlineData("""{"start_temperature": -1}""")]
    [InlineData("""{"start_temperature": "NaN"}""")]
    public void CalibrationParameters_Parse_OutOfRangeOrNonFiniteTemperature_FallsBackToDefault(string json)
    {
        // A non-numeric value at a numeric key falls back to that key's own default under the
        // per-key JsonElement parse (see CalibrationParameters.Parse's <remarks>); numeric
        // out-of-range values fall back via the bounds check. Either way the result must be the
        // safe default.
        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.TemperatureTower);

        parameters.StartTemperatureC.Should().Be(230);
    }

    [Fact]
    public void CalibrationParameters_Parse_Retraction_NullJson_ReturnsDefaults()
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(null, CalibrationMethod.Retraction);

        parameters.StartRetractionMm.Should().Be(0.2);
        parameters.RetractionStepMm.Should().Be(0.2);
        parameters.RetractionBandHeightMm.Should().Be(5);
        parameters.RetractionBandCount.Should().Be(8);
    }

    [Fact]
    public void CalibrationParameters_Parse_Retraction_OverridesProvidedKeysOnly()
    {
        string json = """{"start_retraction_mm": 0.5, "retraction_band_count": 4}""";

        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.Retraction);

        parameters.StartRetractionMm.Should().Be(0.5);
        parameters.RetractionBandCount.Should().Be(4);
        parameters.RetractionStepMm.Should().Be(0.2); // default preserved
        parameters.RetractionBandHeightMm.Should().Be(5); // default preserved
    }

    [Theory]
    [InlineData("""{"start_retraction_mm": 99999}""")]
    [InlineData("""{"start_retraction_mm": -1}""")]
    public void CalibrationParameters_Parse_Retraction_OutOfRangeStartRetraction_FallsBackToDefault(string json)
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.Retraction);

        parameters.StartRetractionMm.Should().Be(0.2, "an adversarial or out-of-range start_retraction_mm must never reach the gcode builder");
    }

    [Theory]
    [InlineData("""{"retraction_step_mm": 99999}""")]
    [InlineData("""{"retraction_step_mm": -1}""")]
    public void CalibrationParameters_Parse_Retraction_OutOfRangeStep_FallsBackToDefault(string json)
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.Retraction);

        parameters.RetractionStepMm.Should().Be(0.2, "an adversarial or out-of-range retraction_step_mm must never reach the gcode builder");
    }

    [Theory]
    [InlineData("""{"retraction_band_height_mm": 99999}""")]
    [InlineData("""{"retraction_band_height_mm": 0}""")]
    public void CalibrationParameters_Parse_Retraction_OutOfRangeBandHeight_FallsBackToDefault(string json)
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.Retraction);

        parameters.RetractionBandHeightMm.Should().Be(5, "an adversarial or out-of-range retraction_band_height_mm must never reach the gcode builder");
    }

    [Theory]
    [InlineData("""{"retraction_band_count": 100000}""")] // absurdly large: would blow up gcode-template generation
    [InlineData("""{"retraction_band_count": 0}""")] // below the minimum of 1
    [InlineData("""{"retraction_band_count": -5}""")]
    public void CalibrationParameters_Parse_Retraction_OutOfRangeBandCount_FallsBackToDefault(string json)
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.Retraction);

        parameters.RetractionBandCount.Should().Be(8, "an adversarial or out-of-range retraction_band_count must never reach the gcode builder");
    }

    [Fact]
    public void CalibrationParameters_Parse_Retraction_InRangeFieldsButOutOfRangeComputedTopBand_FallsBackToAllDefaults()
    {
        // Each individual field below passes its own per-field bound check (start <= 10,
        // step <= 5, band count <= 50), but the *computed* top-band retraction they combine to
        // (8 + 9*1 = 17mm) exceeds MaxRetractionMm (10mm) — a value no real printer's firmware
        // retraction range would ever need. This must fall back to the full default set, not
        // silently clamp only the offending field while leaving the others client-controlled.
        string json = """{"start_retraction_mm": 8, "retraction_step_mm": 1, "retraction_band_count": 10}""";

        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.Retraction);

        parameters.StartRetractionMm.Should().Be(0.2);
        parameters.RetractionStepMm.Should().Be(0.2);
        parameters.RetractionBandHeightMm.Should().Be(5);
        parameters.RetractionBandCount.Should().Be(8);
    }

    [Fact]
    public void CalibrationParameters_Parse_Retraction_ComputedTopBandExactlyAtBound_IsNotClamped()
    {
        // The inclusive edge of ClampRetractionTopBand's `> MaxRetractionMm` check: a computed
        // top band of exactly 10mm (2 + 4*2) must pass through unmodified, not be treated as
        // out-of-range. Only a computed top band that strictly exceeds 10mm should fall back to
        // defaults (covered separately by the 17mm case above).
        string json = """{"start_retraction_mm": 2, "retraction_step_mm": 2, "retraction_band_count": 5}""";

        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.Retraction);

        parameters.StartRetractionMm.Should().Be(2);
        parameters.RetractionStepMm.Should().Be(2);
        parameters.RetractionBandCount.Should().Be(5);
    }

    [Fact]
    public void CalibrationParameters_Parse_PressureAdvanceTower_NullJson_ReturnsDefaults()
    {
        CalibrationParameters parameters = CalibrationParameters.Parse(null, CalibrationMethod.PressureAdvanceTower);

        parameters.StartAdvance.Should().Be(0.0);
        parameters.AdvanceStep.Should().Be(0.002);
        parameters.BandHeightMm.Should().Be(5);
        parameters.BandCount.Should().Be(20);
    }

    [Fact]
    public void CalibrationParameters_Parse_PressureAdvanceTower_OverridesProvidedKeysOnly()
    {
        string json = """{"start_advance": 0.01, "advance_step": 0.005, "band_count": 10}""";

        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.PressureAdvanceTower);

        parameters.StartAdvance.Should().Be(0.01);
        parameters.AdvanceStep.Should().Be(0.005);
        parameters.BandCount.Should().Be(10);
        parameters.BandHeightMm.Should().Be(5); // default preserved
    }

    [Theory]
    [InlineData("""{"start_advance": -1}""")] // below the CalibrationMeasurementRanges.PressureAdvance minimum (0.0)
    [InlineData("""{"start_advance": 2.5}""")] // above the CalibrationMeasurementRanges.PressureAdvance maximum (2.0)
    [InlineData("""{"start_advance": "NaN"}""")] // non-numeric value for a numeric key: only this
                                                 // key falls back to its own default under the
                                                 // per-key JsonElement parse (other keys, if any,
                                                 // are unaffected)
    public void CalibrationParameters_Parse_PressureAdvanceTower_OutOfRangeStartAdvance_FallsBackToDefault(string json)
    {
        // Mirrors Farm.Modules.Calibration.Services.Calibration.CalibrationMeasurementRanges.PressureAdvance
        // (0.0-2.0), so the worker's own bounds-checking never diverges from the saga layer's.
        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.PressureAdvanceTower);

        parameters.StartAdvance.Should().Be(0.0);
    }

    [Fact]
    public void CalibrationParameters_Parse_PressureAdvanceTower_CompoundingAdvanceClampedToMaxAdvance()
    {
        // Regression: each of StartAdvance/AdvanceStep/BandCount is individually in-bounds, and
        // there is genuine headroom below MaxAdvance, so the combination should be honoured by
        // shrinking AdvanceStep rather than refused -- the topmost band's compounded effect
        // (StartAdvance + (BandCount - 1) * AdvanceStep) must never exceed the shared
        // CalibrationMeasurementRanges.PressureAdvance maximum of 2.0, since that value is
        // embedded directly into a SET_PRESSURE_ADVANCE/M900 K gcode command sent to the printer.
        string json = """{"start_advance": 0.0, "advance_step": 0.5, "band_count": 50}""";

        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.PressureAdvanceTower);

        parameters.BandCount.Should().Be(50);
        parameters.StartAdvance.Should().Be(0.0);
        parameters.AdvanceStep.Should().BeLessThan(0.5); // clamped down from the requested step
        parameters.AdvanceStep.Should().BeGreaterThan(0.0); // still a genuine, distinguishable sweep
        double topmostBandAdvance = parameters.StartAdvance + ((parameters.BandCount - 1) * parameters.AdvanceStep);
        topmostBandAdvance.Should().BeLessThanOrEqualTo(2.0);
    }

    [Fact]
    public void CalibrationParameters_Parse_PressureAdvanceTower_NoHeadroomForDistinguishableSweep_ThrowsRatherThanSilentlyProducingIdenticalBands()
    {
        // Regression: if StartAdvance already leaves no room for even the smallest meaningful
        // AdvanceStep across BandCount bands, silently clamping AdvanceStep to (near) zero would
        // produce a "tower" whose bands all emit the same advance value -- a calibration print
        // that runs to completion and reports success while measuring nothing distinguishable.
        // That is exactly the silent-no-op failure mode this calibration method must refuse
        // instead of hiding, so this must throw rather than return a degenerate result.
        string json = """{"start_advance": 2.0, "advance_step": 0.5, "band_count": 50}""";

        Action act = () => CalibrationParameters.Parse(json, CalibrationMethod.PressureAdvanceTower);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().ContainAll("start_advance", "band_count");
    }

    #endregion

    #region FlowRateCalibrationConfigurator — pure parsing

    [Theory]
    [InlineData("flowrate_95", 0.95)]
    [InlineData("flowrate-102.5", 1.025)]
    [InlineData("flow_rate_100", 1.0)]
    [InlineData("Body_1", null)]
    [InlineData(null, null)]
    public void TryParseFlowRatio_ParsesEmbeddedPercentage(string? objectName, double? expectedRatio)
    {
        double? ratio = FlowRateCalibrationConfigurator.TryParseFlowRatio(objectName);

        ratio.Should().Be(expectedRatio);
    }

    [Fact]
    public void ParseObjectNames_ParsesIdsAndNamesFromCoreModelXml()
    {
        string modelXml = BuildModelXml(("1", "flowrate_90"), ("2", "flowrate_95"));

        IReadOnlyList<(int Id, string? Name)> objects = FlowRateCalibrationConfigurator.ParseObjectNames(modelXml);

        objects.Should().BeEquivalentTo(new[] { (1, "flowrate_90"), (2, "flowrate_95") });
    }

    [Fact]
    public void BuildObjectConfigXml_MergesFlowRatiosWithoutClobberingUnrelatedObjects()
    {
        string existing = """
            <?xml version="1.0" encoding="UTF-8"?>
            <config>
              <object id="7">
                <metadata type="object" key="name" value="unrelated"/>
              </object>
            </config>
            """;

        string result = FlowRateCalibrationConfigurator.BuildObjectConfigXml(
            new Dictionary<int, double> { [1] = 0.9, [2] = 0.95 },
            existing);

        result.Should().Contain("id=\"7\"");
        result.Should().Contain("unrelated");
        result.Should().Contain("id=\"1\"");
        result.Should().Contain("flow_ratio");
        result.Should().Contain("0.9");
        result.Should().Contain("0.95");
    }

    #endregion

    #region FlowRateCalibrationConfigurator — end-to-end 3MF, nine distinct blocks

    [Fact]
    public void ApplyPerObjectFlowRatios_NineNamedObjects_ProducesNineDistinctFlowRatios()
    {
        // The acceptance criterion for issue #1938 is explicit: a flow-rate calibration slice must
        // differ per block, since "sliced successfully" and "sliced correctly" look identical
        // otherwise. This builds a synthetic 9-object 3MF (mirroring the real
        // flowrate-test-pass1.3mf's nine printed blocks) and asserts nine distinct flow_ratio
        // overrides come out the other end.
        (string Id, string Name)[] blocks =
        [
            ("1", "flowrate_90"),
            ("2", "flowrate_92.5"),
            ("3", "flowrate_95"),
            ("4", "flowrate_97.5"),
            ("5", "flowrate_100"),
            ("6", "flowrate_102.5"),
            ("7", "flowrate_105"),
            ("8", "flowrate_107.5"),
            ("9", "flowrate_110"),
        ];
        string source3mf = CreateSynthetic3mf(blocks);

        string resultPath = FlowRateCalibrationConfigurator.ApplyPerObjectFlowRatios(
            source3mf,
            _tempDir,
            NullLogger.Instance);

        using ZipArchive archive = ZipFile.OpenRead(resultPath);
        ZipArchiveEntry configEntry = archive.GetEntry("Metadata/Slic3r_PE_model.config")!;
        string configXml = ReadEntryText(configEntry);

        System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Parse(configXml);
        List<double> flowRatios = doc.Root!.Elements("object")
            .Select(o => o.Elements("metadata")
                .First(m => m.Attribute("key")!.Value == "flow_ratio")
                .Attribute("value")!.Value)
            .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        flowRatios.Should().HaveCount(9);
        flowRatios.Distinct().Should().HaveCount(9, "each block must be configured with a different flow ratio");
    }

    [Fact]
    public void ApplyPerObjectFlowRatios_NoParseableNames_ThrowsRatherThanSlicingWithoutOverrides()
    {
        // A calibration slice that "succeeds" without per-object overrides would silently produce
        // identical G-code for every block — the acceptance criterion is explicit that the nine
        // blocks must differ, so this must fail loudly instead of degrading silently.
        string source3mf = CreateSynthetic3mf([("1", "Body")]);

        Action act = () => FlowRateCalibrationConfigurator.ApplyPerObjectFlowRatios(
            source3mf,
            _tempDir,
            NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*flow-rate*")
            .Which.Message.Should().NotContain(_tempDir, "the exception message must not disclose internal worker filesystem paths");
    }

    [Fact]
    public void ApplyPerObjectFlowRatios_MissingModelEntry_Throws()
    {
        string sourceDir = Path.Combine(_tempDir, "source-no-model");
        Directory.CreateDirectory(sourceDir);
        string path = Path.Join(sourceDir, $"empty-{Guid.NewGuid():N}.3mf");
        using (FileStream fs = File.Create(path))
        using (_ = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            // Intentionally no entries: this 3MF has no 3D/3dmodel.model.
        }

        Action act = () => FlowRateCalibrationConfigurator.ApplyPerObjectFlowRatios(
            path,
            _tempDir,
            NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain(_tempDir, "the exception message must not disclose internal worker filesystem paths");
    }

    #endregion

    #region FlowRateDeltaCalibrationConfigurator — pure parsing

    [Theory]
    [InlineData("flowrate_0.01", 0.01)]
    [InlineData("flowrate_m0.01", -0.01)]
    [InlineData("flowrate_0.05", 0.05)]
    [InlineData("flowrate_m0.05", -0.05)]
    [InlineData("flow_rate_m1.5", -1.5)]
    [InlineData("flow-rate_2", 2.0)]
    [InlineData("FLOWRATE_M0.02", -0.02)]
    [InlineData("flowrate_0.005", 0.005)]
    [InlineData("flowrate_m0.035", -0.035)]
    [InlineData("flowrate_m0.005", -0.005)]
    [InlineData("Body_1", null)]
    [InlineData(null, null)]
    public void TryParseFlowDelta_ParsesSignedBaselineRelativeDelta(string? objectName, double? expectedDelta)
    {
        // Issue #2141: unlike FlowRateCalibrationConfigurator.TryParseFlowRatio (which divides an
        // embedded percentage by 100), this must return the raw additive delta unscaled — the
        // "m" prefix stands in for a minus sign that cannot appear in a 3MF object name.
        // Issue #2142 (Perfectionist): the finer 0.005 step size means object names carry three
        // decimal places (e.g. "flowrate_0.005", "flowrate_m0.035") rather than Recommended's two
        // (e.g. "flowrate_0.01") — the regex's "\d+(?:\.\d+)?" group is not limited to a fixed
        // decimal count, so these must parse without any parser change.
        double? delta = FlowRateDeltaCalibrationConfigurator.TryParseFlowDelta(objectName);

        delta.Should().Be(expectedDelta);
    }

    [Fact]
    public void ResolveObjectFlowRatios_ValidDeltas_AppliesBaselinePlusDeltaPerObject()
    {
        IReadOnlyDictionary<int, double> ratios = FlowRateDeltaCalibrationConfigurator.ResolveObjectFlowRatios(
            [(1, "flowrate_0.01"), (2, "flowrate_m0.01")],
            baselineFlowRatio: 0.98);

        ratios.Should().BeEquivalentTo(new Dictionary<int, double>
        {
            [1] = 0.99,
            [2] = 0.97,
        });
    }

    [Fact]
    public void ResolveObjectFlowRatios_UnparseableObjectName_ThrowsRatherThanGuessing()
    {
        // Control case (issue #2141 acceptance criterion): an object name that carries no
        // recognizable delta must refuse loudly rather than silently defaulting to the baseline
        // (which would produce an uncalibrated, misleading flow ratio for that object).
        Action act = () => FlowRateDeltaCalibrationConfigurator.ResolveObjectFlowRatios(
            [(1, "flowrate_0.01"), (2, "Body_2")],
            baselineFlowRatio: 0.98);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Body_2*")
            .Which.Message.Should().Contain(
                "flowrate_",
                "the message should explain the expected naming scheme, not just that parsing failed");
    }

    #endregion

    #region FlowRateDeltaCalibrationConfigurator — end-to-end 3MF

    [Fact]
    public void ApplyPerObjectFlowRatioDeltas_PositiveAndNegativeDeltas_AppliesBaselinePlusDelta()
    {
        (string Id, string Name)[] objects =
        [
            ("1", "flowrate_0.01"),
            ("2", "flowrate_m0.01"),
            ("3", "flowrate_0.05"),
        ];
        string source3mf = CreateSynthetic3mf(objects);

        string resultPath = FlowRateDeltaCalibrationConfigurator.ApplyPerObjectFlowRatioDeltas(
            source3mf,
            _tempDir,
            baselineFlowRatio: 0.98,
            NullLogger.Instance);

        using ZipArchive archive = ZipFile.OpenRead(resultPath);
        ZipArchiveEntry configEntry = archive.GetEntry("Metadata/Slic3r_PE_model.config")!;
        string configXml = ReadEntryText(configEntry);

        System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Parse(configXml);
        Dictionary<int, double> flowRatiosById = doc.Root!.Elements("object")
            .ToDictionary(
                o => int.Parse(o.Attribute("id")!.Value, System.Globalization.CultureInfo.InvariantCulture),
                o => double.Parse(
                    o.Elements("metadata").First(m => m.Attribute("key")!.Value == "flow_ratio").Attribute("value")!.Value,
                    System.Globalization.CultureInfo.InvariantCulture));

        flowRatiosById.Should().BeEquivalentTo(new Dictionary<int, double>
        {
            [1] = 0.99,
            [2] = 0.97,
            [3] = 1.03,
        });
    }

    [Fact]
    public void ApplyPerObjectFlowRatioDeltas_ThreeDecimalPerfectionistDeltas_AppliesBaselinePlusDelta()
    {
        // Issue #2142 acceptance criterion: Perfectionist's finer 0.005 step size means bundled
        // object names carry three decimal places (e.g. "flowrate_0.005", "flowrate_m0.035")
        // rather than Recommended's two (e.g. "flowrate_0.01") — this must parse and apply
        // correctly, both positive and negative, without any parser change.
        (string Id, string Name)[] objects =
        [
            ("1", "flowrate_0.005"),
            ("2", "flowrate_m0.005"),
            ("3", "flowrate_m0.035"),
        ];
        string source3mf = CreateSynthetic3mf(objects);

        string resultPath = FlowRateDeltaCalibrationConfigurator.ApplyPerObjectFlowRatioDeltas(
            source3mf,
            _tempDir,
            baselineFlowRatio: 0.98,
            NullLogger.Instance);

        using ZipArchive archive = ZipFile.OpenRead(resultPath);
        ZipArchiveEntry configEntry = archive.GetEntry("Metadata/Slic3r_PE_model.config")!;
        string configXml = ReadEntryText(configEntry);

        System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Parse(configXml);
        Dictionary<int, double> flowRatiosById = doc.Root!.Elements("object")
            .ToDictionary(
                o => int.Parse(o.Attribute("id")!.Value, System.Globalization.CultureInfo.InvariantCulture),
                o => double.Parse(
                    o.Elements("metadata").First(m => m.Attribute("key")!.Value == "flow_ratio").Attribute("value")!.Value,
                    System.Globalization.CultureInfo.InvariantCulture));

        flowRatiosById.Should().BeEquivalentTo(new Dictionary<int, double>
        {
            [1] = 0.985,
            [2] = 0.975,
            [3] = 0.945,
        });
    }

    [Fact]
    public void ApplyPerObjectFlowRatioDeltas_UnparseableObjectName_ThrowsWithoutLeakingPath()
    {
        // Control case mirroring FlowRateCalibrationConfigurator's equivalent test: an
        // unparseable name must fail the whole job rather than silently applying the baseline
        // (which would defeat the point of a per-object delta calibration).
        string source3mf = CreateSynthetic3mf([("1", "Body")]);

        Action act = () => FlowRateDeltaCalibrationConfigurator.ApplyPerObjectFlowRatioDeltas(
            source3mf,
            _tempDir,
            baselineFlowRatio: 0.98,
            NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Body*")
            .Which.Message.Should().NotContain(_tempDir, "the exception message must not disclose internal worker filesystem paths");
    }

    [Fact]
    public void ApplyPerObjectFlowRatioDeltas_MissingModelEntry_Throws()
    {
        string sourceDir = Path.Combine(_tempDir, "source-no-model-delta");
        Directory.CreateDirectory(sourceDir);
        string path = Path.Join(sourceDir, $"empty-{Guid.NewGuid():N}.3mf");
        using (FileStream fs = File.Create(path))
        using (_ = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            // Intentionally no entries: this 3MF has no 3D/3dmodel.model.
        }

        Action act = () => FlowRateDeltaCalibrationConfigurator.ApplyPerObjectFlowRatioDeltas(
            path,
            _tempDir,
            baselineFlowRatio: 0.98,
            NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain(_tempDir, "the exception message must not disclose internal worker filesystem paths");
    }

    #endregion

    #region OrcaSlicingPipelineService — calibration pipeline wiring

    [Theory]
    [InlineData(CalibrationMethod.FlowRatePass1, "flowrate-test-pass1.3mf")]
    [InlineData(CalibrationMethod.FlowRatePass2, "flowrate-test-pass2.3mf")]
    public void PrepareCalibrationModel_FlowRateMethod_ResolvesResourceAndAppliesFlowRatios(
        CalibrationMethod method,
        string resourceFileName)
    {
        // Regression coverage for the actual worker pipeline wiring (issue #1938): a unit test on
        // FlowRateCalibrationConfigurator alone cannot catch a regression in how
        // OrcaSlicingPipelineService resolves the calibration method, locates the bundled
        // resource, and routes flow-rate methods through the per-object configurator.
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources");
        string flowRatePath = Path.Combine(calibResourcesRoot, "filament_flow", resourceFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(flowRatePath)!);
        CreateSynthetic3mfAt(flowRatePath, [("1", "flowrate_90"), ("2", "flowrate_110")]);

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(method),
        };

        string preparedPath = pipeline.PrepareCalibrationModel(job, workDir);

        File.Exists(preparedPath).Should().BeTrue();
        using ZipArchive archive = ZipFile.OpenRead(preparedPath);
        archive.GetEntry("Metadata/Slic3r_PE_model.config").Should().NotBeNull(
            "the flow-rate path must produce a 3MF with per-object flow_ratio overrides, not a plain copy");
    }

    [Fact]
    public void PrepareCalibrationModel_TemperatureTowerMethod_CopiesResourceUnmodified()
    {
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-tt");
        string towerPath = Path.Combine(calibResourcesRoot, "temperature_tower", "temperature_tower.drc");
        Directory.CreateDirectory(Path.GetDirectoryName(towerPath)!);
        File.WriteAllText(towerPath, "fake-tower-resource");

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.TemperatureTower),
        };

        string preparedPath = pipeline.PrepareCalibrationModel(job, workDir);

        File.Exists(preparedPath).Should().BeTrue();
        File.ReadAllText(preparedPath).Should().Be("fake-tower-resource");
    }

    [Fact]
    public void PrepareCalibrationModel_RetractionMethod_CopiesResourceUnmodified()
    {
        // Mirrors PrepareCalibrationModel_TemperatureTowerMethod_CopiesResourceUnmodified, but
        // compares raw bytes rather than text: the bundled retraction_tower.drc resource is a
        // binary Draco mesh (issue #2137), and a text-based round trip could mask corruption from
        // an encoding conversion that a byte-for-byte comparison would catch immediately.
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-retraction");
        string towerPath = Path.Combine(calibResourcesRoot, "retraction", "retraction_tower.drc");
        Directory.CreateDirectory(Path.GetDirectoryName(towerPath)!);
        byte[] fakeDracoBytes = [0x44, 0x52, 0x41, 0x43, 0x4F, 0x00, 0xFF, 0xFE, 0x80, 0x01, 0x02, 0x03];
        File.WriteAllBytes(towerPath, fakeDracoBytes);

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.Retraction),
        };

        string preparedPath = pipeline.PrepareCalibrationModel(job, workDir);

        File.Exists(preparedPath).Should().BeTrue();
        File.ReadAllBytes(preparedPath).Should().Equal(fakeDracoBytes, "a binary Draco mesh must survive the copy byte-for-byte, not just as valid text");
    }

    [Fact]
    public void PrepareCalibrationModel_PressureAdvanceTowerMethod_CopiesResourceUnmodified()
    {
        // Mirrors PrepareCalibrationModel_TemperatureTowerMethod_CopiesResourceUnmodified: the
        // pressure advance tower method's per-band configuration is injected later, into the
        // process profile in RunOrcaSlicerAsync (see ApplyPressureAdvanceTowerGcodeAsync below),
        // so PrepareCalibrationModel just needs to copy the bundled resource unmodified.
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-pa");
        string towerPath = Path.Combine(calibResourcesRoot, "pressure_advance", "tower_with_seam.drc");
        Directory.CreateDirectory(Path.GetDirectoryName(towerPath)!);
        File.WriteAllText(towerPath, "fake-pa-tower-resource");

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.PressureAdvanceTower),
        };

        string preparedPath = pipeline.PrepareCalibrationModel(job, workDir);

        File.Exists(preparedPath).Should().BeTrue();
        File.ReadAllText(preparedPath).Should().Be("fake-pa-tower-resource");
    }

    [Fact]
    public async Task PrepareCalibrationModelThenApplyPressureAdvanceTowerGcodeAsync_EndToEndAcrossBothPipelineStages_ProducesModelAndInjectedGcodeFromTheSameJob()
    {
        // Cross-stage regression (issue #2136 acceptance criterion: "wire name submits/slices/
        // returns gcode end to end"). The two tests above cover PrepareCalibrationModel and
        // ApplyPressureAdvanceTowerGcodeAsync in isolation; this test drives the SAME job instance
        // through both real pipeline entrypoints in the order OrcaSlicingPipelineService itself
        // calls them (model preparation, then process-profile gcode injection ahead of the actual
        // OrcaSlicer CLI invocation), proving the two stages compose correctly end to end rather
        // than merely each behaving correctly alone. It intentionally does not invoke the real
        // OrcaSlicer binary -- that is exercised by the worker's own CLI-integration coverage, and
        // duplicating it here would only reintroduce the process-execution flakiness the rest of
        // this suite deliberately avoids by testing the pipeline's C# wiring directly.
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-pa-e2e");
        string towerPath = Path.Combine(calibResourcesRoot, "pressure_advance", "tower_with_seam.drc");
        Directory.CreateDirectory(Path.GetDirectoryName(towerPath)!);
        File.WriteAllText(towerPath, "fake-pa-tower-resource");
        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.PressureAdvanceTower),
            CalibrationParamsJson = """{"start_advance": 0.0, "advance_step": 0.01, "band_height_mm": 5, "band_count": 4}""",
        };

        // Stage 1: model resolution/preparation, exactly as OrcaSlicingPipelineService performs it
        // before handing the job to the OrcaSlicer CLI.
        string preparedModelPath = pipeline.PrepareCalibrationModel(job, workDir);

        File.Exists(preparedModelPath).Should().BeTrue("the prepared 3MF/resource must exist before slicing can proceed");
        File.ReadAllText(preparedModelPath).Should().Be("fake-pa-tower-resource");

        // Stage 2: process-profile gcode injection, exactly as OrcaSlicingPipelineService performs
        // it (via RunOrcaSlicerAsync) immediately before invoking the OrcaSlicer CLI with the
        // prepared model from stage 1 and the mutated process profile from this stage.
        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(processJsonPath, """{"name": "Test Process"}""");
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(machineJsonPath, """{"name": "Test Machine", "gcode_flavor": "klipper"}""");

        await OrcaSlicingPipelineService.ApplyPressureAdvanceTowerGcodeAsync(job, processJsonPath, machineJsonPath, CancellationToken.None);

        string updatedProcessContent = await File.ReadAllTextAsync(processJsonPath);
        using JsonDocument doc = JsonDocument.Parse(updatedProcessContent);
        string layerChangeGcode = doc.RootElement.GetProperty("layer_change_gcode").GetString()!;
        layerChangeGcode.Should().Contain(
            "SET_PRESSURE_ADVANCE",
            "the same job's CalibrationParamsJson (parsed once, upstream of both stages) must drive " +
            "the actual injected gcode -- proving the prepared model and the injected gcode both " +
            "trace back to one coherent calibration request, not two independently-configured stages");
        job.ProcessProfileSha256.Should().Be(
            NativeSlicerProfiles.ComputeSha256(updatedProcessContent),
            "the job's recorded digest must reflect the process profile as mutated by stage 2, " +
            "ready for the OrcaSlicer CLI invocation that would follow with the stage-1 model path");
    }

    [Fact]
    public void PrepareCalibrationModel_YoloPerfectionistMethod_AppliesBaselinePlusDeltaFlowRatios()
    {
        // Issue #2142 acceptance criterion: FlowRateYoloPerfectionist must now actually apply the
        // bundled resource's per-object flow-ratio deltas (baseline + delta) end to end through
        // the real pipeline entrypoint, using Perfectionist's finer three-decimal-place naming
        // (e.g. "flowrate_0.005", "flowrate_m0.035") — mirroring
        // PrepareCalibrationModel_YoloRecommendedMethod_AppliesBaselinePlusDeltaFlowRatios below,
        // which is the equivalent golden-path coverage for Recommended (issue #2141).
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-yolo-perfectionist-" + Guid.NewGuid().ToString("N"));
        string resourcePath = Path.Combine(calibResourcesRoot, "filament_flow", "Orca-LinearFlow_fine.3mf");
        Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
        CreateSynthetic3mfAt(resourcePath, [("1", "flowrate_0.005"), ("2", "flowrate_m0.035")]);

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.FlowRateYoloPerfectionist),
            Profile = new SlicerProfileDto
            {
                FilamentProfile = new FilamentProfileDto { FlowRatio = 0.98 },
            },
        };

        string preparedPath = pipeline.PrepareCalibrationModel(job, workDir);

        File.Exists(preparedPath).Should().BeTrue();
        using ZipArchive archive = ZipFile.OpenRead(preparedPath);
        ZipArchiveEntry configEntry = archive.GetEntry("Metadata/Slic3r_PE_model.config")!;
        string configXml = ReadEntryText(configEntry);
        System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Parse(configXml);
        Dictionary<int, double> flowRatiosById = doc.Root!.Elements("object")
            .ToDictionary(
                o => int.Parse(o.Attribute("id")!.Value, System.Globalization.CultureInfo.InvariantCulture),
                o => double.Parse(
                    o.Elements("metadata").First(m => m.Attribute("key")!.Value == "flow_ratio").Attribute("value")!.Value,
                    System.Globalization.CultureInfo.InvariantCulture));

        flowRatiosById.Should().BeEquivalentTo(new Dictionary<int, double>
        {
            [1] = 0.985,
            [2] = 0.945,
        });
    }

    [Fact]
    public void PrepareCalibrationModel_YoloPerfectionistMethod_UnparseableObjectName_ThrowsWithoutLeakingPath()
    {
        // Issue #2142 acceptance criterion (control case): an unparseable object name in the
        // bundled resource must still refuse loudly through the real pipeline entrypoint, not
        // just the shared static configurator tested in isolation above — and the failure must
        // not disclose internal worker filesystem paths, mirroring every other calibration
        // method's equivalent control test in this file.
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-yolo-perfectionist-badname-" + Guid.NewGuid().ToString("N"));
        string resourcePath = Path.Combine(calibResourcesRoot, "filament_flow", "Orca-LinearFlow_fine.3mf");
        Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
        CreateSynthetic3mfAt(resourcePath, [("1", "flowrate_0.005"), ("2", "Body_2")]);

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.FlowRateYoloPerfectionist),
            Profile = new SlicerProfileDto
            {
                FilamentProfile = new FilamentProfileDto { FlowRatio = 0.98 },
            },
        };

        Action act = () => pipeline.PrepareCalibrationModel(job, workDir);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Body_2*")
            .Which.Message.Should().NotContain(
                calibResourcesRoot,
                "the exception message must not disclose internal worker filesystem paths");
    }

    [Fact]
    public void PrepareCalibrationModel_YoloRecommendedMethod_AppliesBaselinePlusDeltaFlowRatios()
    {
        // Issue #2141 acceptance criterion: FlowRateYoloRecommended must now actually apply the
        // bundled resource's per-object flow-ratio deltas (baseline + delta), not throw.
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-yolo-recommended-" + Guid.NewGuid().ToString("N"));
        string resourcePath = Path.Combine(calibResourcesRoot, "filament_flow", "Orca-LinearFlow.3mf");
        Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
        CreateSynthetic3mfAt(resourcePath, [("1", "flowrate_0.01"), ("2", "flowrate_m0.01")]);

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.FlowRateYoloRecommended),
            Profile = new SlicerProfileDto
            {
                FilamentProfile = new FilamentProfileDto { FlowRatio = 0.98 },
            },
        };

        string preparedPath = pipeline.PrepareCalibrationModel(job, workDir);

        File.Exists(preparedPath).Should().BeTrue();
        using ZipArchive archive = ZipFile.OpenRead(preparedPath);
        ZipArchiveEntry configEntry = archive.GetEntry("Metadata/Slic3r_PE_model.config")!;
        string configXml = ReadEntryText(configEntry);
        System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Parse(configXml);
        Dictionary<int, double> flowRatiosById = doc.Root!.Elements("object")
            .ToDictionary(
                o => int.Parse(o.Attribute("id")!.Value, System.Globalization.CultureInfo.InvariantCulture),
                o => double.Parse(
                    o.Elements("metadata").First(m => m.Attribute("key")!.Value == "flow_ratio").Attribute("value")!.Value,
                    System.Globalization.CultureInfo.InvariantCulture));

        flowRatiosById.Should().BeEquivalentTo(new Dictionary<int, double>
        {
            [1] = 0.99,
            [2] = 0.97,
        });
    }

    [Fact]
    public void PrepareCalibrationModel_YoloRecommendedMethod_NoResolvableBaseline_ThrowsRatherThanGuessing()
    {
        // A delta-based calibration slice with a guessed baseline (e.g. defaulting to 1.0) could
        // silently apply the wrong flow ratio to every object; the worker must refuse instead.
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-yolo-nobaseline-" + Guid.NewGuid().ToString("N"));
        string resourcePath = Path.Combine(calibResourcesRoot, "filament_flow", "Orca-LinearFlow.3mf");
        Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
        CreateSynthetic3mfAt(resourcePath, [("1", "flowrate_0.01")]);

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.FlowRateYoloRecommended),
        };

        Action act = () => pipeline.PrepareCalibrationModel(job, workDir);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*filament_flow_ratio*")
            .Which.Message.Should().Contain(
                "Select a filament profile that carries a flow ratio, or refuse rather than guessing one",
                "the refusal message must explain the fix (pick a profile with a flow ratio), not just " +
                "name the missing field");
    }

    [Fact]
    public void ResolveBaselineFlowRatio_ExtruderFilamentProfilesPresent_PrefersExtruderZeroOverFilamentProfile()
    {
        // GenerateProfileJsonFilesAsync (issue #2141 review finding) prefers
        // ExtruderFilamentProfiles[0] over FilamentProfile whenever an extruder profile is present
        // — the baseline resolution must mirror that precedence or it would report a flow ratio
        // that is not the one OrcaSlicer actually slices with.
        var job = new DistributedSlicingJob
        {
            Profile = new SlicerProfileDto
            {
                FilamentProfile = new FilamentProfileDto { FlowRatio = 0.5 },
                ExtruderFilamentProfiles = [new FilamentProfileDto { FlowRatio = 0.98 }],
            },
        };

        OrcaSlicingPipelineService.ResolveBaselineFlowRatio(job).Should().Be(0.98);
    }

    [Fact]
    public void ResolveBaselineFlowRatio_NoExtruderProfiles_FallsBackToFilamentProfile()
    {
        var job = new DistributedSlicingJob
        {
            Profile = new SlicerProfileDto
            {
                FilamentProfile = new FilamentProfileDto { FlowRatio = 0.93 },
            },
        };

        OrcaSlicingPipelineService.ResolveBaselineFlowRatio(job).Should().Be(0.93);
    }

    [Fact]
    public void ResolveBaselineFlowRatio_NativeProfilesPresent_PrefersNativeOverJobProfile()
    {
        // RunOrcaSlicerAsync writes NativeProfiles verbatim and never consults job.Profile when
        // NativeProfiles is non-null — so the baseline must come from NativeProfiles.FilamentJson
        // in that case, even when job.Profile also carries a (different, worker-cache-resolved)
        // flow ratio. Preferring job.Profile here would silently measure against a filament
        // profile OrcaSlicer never actually slices with (issue #2141 review finding).
        var job = new DistributedSlicingJob
        {
            Profile = new SlicerProfileDto
            {
                FilamentProfile = new FilamentProfileDto { FlowRatio = 0.5 },
            },
            NativeProfiles = new NativeSlicerProfiles(
                MachineJson: "{}",
                ProcessJson: "{}",
                FilamentJson: """{"filament_flow_ratio": "0.98"}""",
                MachineSha256: "m",
                ProcessSha256: "p",
                FilamentSha256: "f"),
        };

        OrcaSlicingPipelineService.ResolveBaselineFlowRatio(job).Should().Be(0.98);
    }

    [Fact]
    public void ResolveBaselineFlowRatio_NativeProfilesPresentAsArray_ParsesFirstElement()
    {
        var job = new DistributedSlicingJob
        {
            NativeProfiles = new NativeSlicerProfiles(
                MachineJson: "{}",
                ProcessJson: "{}",
                FilamentJson: """{"filament_flow_ratio": ["0.965", "1.0"]}""",
                MachineSha256: "m",
                ProcessSha256: "p",
                FilamentSha256: "f"),
        };

        OrcaSlicingPipelineService.ResolveBaselineFlowRatio(job).Should().Be(0.965);
    }

    [Fact]
    public void ResolveBaselineFlowRatio_NativeProfilesArrayElementIsNumberNotString_ParsesWithoutCrashing()
    {
        // A hand-edited native profile could plausibly store filament_flow_ratio as an array of
        // *numbers* (`[1.05]`) rather than OrcaSlicer's normal array-of-strings shape
        // (`["1.05"]`). Blindly calling JsonElement.GetString() on a Number element throws
        // InvalidOperationException, which the surrounding catch (JsonException) does not catch —
        // that would crash the job with a raw framework exception instead of either parsing the
        // number or cleanly falling through to the "no baseline resolved" refusal.
        var job = new DistributedSlicingJob
        {
            NativeProfiles = new NativeSlicerProfiles(
                MachineJson: "{}",
                ProcessJson: "{}",
                FilamentJson: """{"filament_flow_ratio": [1.05]}""",
                MachineSha256: "m",
                ProcessSha256: "p",
                FilamentSha256: "f"),
        };

        OrcaSlicingPipelineService.ResolveBaselineFlowRatio(job).Should().Be(1.05);
    }

    [Fact]
    public void ResolveBaselineFlowRatio_NativeProfilesArrayElementUnrecognizedKind_ThrowsRatherThanGuessing()
    {
        // An array element that is neither a string nor a number (e.g. a nested object or bool) is
        // genuinely unparseable; the worker must refuse rather than silently falling back to
        // job.Profile, since RunOrcaSlicerAsync would still slice with NativeProfiles verbatim.
        var job = new DistributedSlicingJob
        {
            Profile = new SlicerProfileDto
            {
                FilamentProfile = new FilamentProfileDto { FlowRatio = 0.5 },
            },
            NativeProfiles = new NativeSlicerProfiles(
                MachineJson: "{}",
                ProcessJson: "{}",
                FilamentJson: """{"filament_flow_ratio": [true]}""",
                MachineSha256: "m",
                ProcessSha256: "p",
                FilamentSha256: "f"),
        };

        Action act = () => OrcaSlicingPipelineService.ResolveBaselineFlowRatio(job);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*filament_flow_ratio*");
    }

    [Fact]
    public void ResolveBaselineFlowRatio_NativeProfilesMissingFlowRatioProperty_ThrowsRatherThanGuessing()
    {
        var job = new DistributedSlicingJob
        {
            NativeProfiles = new NativeSlicerProfiles(
                MachineJson: "{}",
                ProcessJson: "{}",
                FilamentJson: "{}",
                MachineSha256: "m",
                ProcessSha256: "p",
                FilamentSha256: "f"),
        };

        Action act = () => OrcaSlicingPipelineService.ResolveBaselineFlowRatio(job);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*filament_flow_ratio*");
    }

    [Fact]
    public void ResolveBaselineFlowRatio_NativeProfilesMalformedJson_ThrowsRatherThanGuessing()
    {
        var job = new DistributedSlicingJob
        {
            NativeProfiles = new NativeSlicerProfiles(
                MachineJson: "{}",
                ProcessJson: "{}",
                FilamentJson: "{ not valid json",
                MachineSha256: "m",
                ProcessSha256: "p",
                FilamentSha256: "f"),
        };

        Action act = () => OrcaSlicingPipelineService.ResolveBaselineFlowRatio(job);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*filament_flow_ratio*");
    }

    [Fact]
    public void ResolveBaselineFlowRatio_NativeProfilesJsonRootIsNotAnObject_ThrowsRatherThanGuessing()
    {
        // A valid-JSON-but-non-object root (e.g. a bare array or string) must not crash with a raw
        // InvalidOperationException from TryGetProperty — it should refuse cleanly like any other
        // unparseable native profile.
        var job = new DistributedSlicingJob
        {
            NativeProfiles = new NativeSlicerProfiles(
                MachineJson: "{}",
                ProcessJson: "{}",
                FilamentJson: "[\"1.05\"]",
                MachineSha256: "m",
                ProcessSha256: "p",
                FilamentSha256: "f"),
        };

        Action act = () => OrcaSlicingPipelineService.ResolveBaselineFlowRatio(job);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*filament_flow_ratio*");
    }

    [Fact]
    public void PrepareCalibrationModel_YoloRecommendedMethod_NativeProfilesPresent_UsesNativeBaselineOverJobProfile()
    {
        // End-to-end variant of ResolveBaselineFlowRatio_NativeProfilesPresent_PrefersNativeOverJobProfile
        // through the full PrepareCalibrationModel path, proving the pipeline wiring (not just the
        // helper in isolation) resolves the baseline OrcaSlicer will actually slice with.
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-yolo-native-" + Guid.NewGuid().ToString("N"));
        string resourcePath = Path.Combine(calibResourcesRoot, "filament_flow", "Orca-LinearFlow.3mf");
        Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
        CreateSynthetic3mfAt(resourcePath, [("1", "flowrate_0.01")]);

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.FlowRateYoloRecommended),
            Profile = new SlicerProfileDto
            {
                FilamentProfile = new FilamentProfileDto { FlowRatio = 0.5 },
            },
            NativeProfiles = new NativeSlicerProfiles(
                MachineJson: "{}",
                ProcessJson: "{}",
                FilamentJson: """{"filament_flow_ratio": "0.98"}""",
                MachineSha256: "m",
                ProcessSha256: "p",
                FilamentSha256: "f"),
        };

        string preparedPath = pipeline.PrepareCalibrationModel(job, workDir);

        using ZipArchive archive = ZipFile.OpenRead(preparedPath);
        ZipArchiveEntry configEntry = archive.GetEntry("Metadata/Slic3r_PE_model.config")!;
        string configXml = ReadEntryText(configEntry);
        System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Parse(configXml);
        double appliedFlowRatio = double.Parse(
            doc.Root!.Elements("object").Single()
                .Elements("metadata").First(m => m.Attribute("key")!.Value == "flow_ratio").Attribute("value")!.Value,
            System.Globalization.CultureInfo.InvariantCulture);

        // baseline 0.98 (native) + delta 0.01, NOT baseline 0.5 (job.Profile) + delta 0.01.
        appliedFlowRatio.Should().Be(0.99);
    }

    [Fact]
    public void PrepareCalibrationModel_MissingResourceFile_ThrowsWithoutLeakingPath()
    {
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-missing");
        Directory.CreateDirectory(calibResourcesRoot);
        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.TemperatureTower),
        };

        Action act = () => pipeline.PrepareCalibrationModel(job, workDir);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain(calibResourcesRoot, "the exception message must not disclose internal worker filesystem paths");
    }

    [Fact]
    public async Task ApplyTemperatureTowerGcodeAsync_InjectsLayerChangeGcodeAndRecomputesDigest()
    {
        // Regression coverage for the pipeline's process-profile injection wiring: a unit test on
        // TemperatureTowerGcodeBuilder alone does not prove the gcode actually reaches the process
        // profile on disk, or that the recorded digest is recomputed to match.
        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(processJsonPath, """{"name": "Test Process"}""");
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.TemperatureTower),
            CalibrationParamsJson = """{"start_temperature": 220, "temperature_step": 10, "band_height_mm": 20, "band_count": 3}""",
        };

        await OrcaSlicingPipelineService.ApplyTemperatureTowerGcodeAsync(job, processJsonPath, CancellationToken.None);

        string updatedContent = await File.ReadAllTextAsync(processJsonPath);
        using JsonDocument doc = JsonDocument.Parse(updatedContent);
        string layerChangeGcode = doc.RootElement.GetProperty("layer_change_gcode").GetString()!;
        string expectedGcode = TemperatureTowerGcodeBuilder.BuildLayerChangeGcode(
            startTemperatureC: 220,
            temperatureStepC: 10,
            bandHeightMm: 20,
            bandCount: 3);
        layerChangeGcode.Should().Be(
            expectedGcode,
            "the pipeline must inject the exact gcode computed from the job's CalibrationParamsJson, " +
            "not a default/fallback template — a bug that ignores the client-supplied band_height_mm/" +
            "band_count/temperature_step would otherwise go undetected because the default 9-band " +
            "template also happens to contain a 'layer_z >= 40' threshold");
        job.ProcessProfileSha256.Should().NotBeNullOrEmpty();
        job.ProcessProfileSha256.Should().Be(
            NativeSlicerProfiles.ComputeSha256(updatedContent),
            "the recorded digest must match the mutated process profile content, not the original");
    }

    [Theory]
    [InlineData("klipper")]
    [InlineData("marlin")]
    [InlineData("marlin2")]
    public async Task ApplyPressureAdvanceTowerGcodeAsync_InjectsLayerChangeGcodeAndRecomputesDigest(string gcodeFlavor)
    {
        // Regression coverage for the pipeline's process-profile injection wiring, mirroring
        // ApplyTemperatureTowerGcodeAsync_InjectsLayerChangeGcodeAndRecomputesDigest — plus proof
        // that the correct firmware-specific command is chosen from the machine profile's
        // gcode_flavor (issue #2136's firmware-flavour decision).
        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(processJsonPath, """{"name": "Test Process"}""");
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(machineJsonPath, $$"""{"name": "Test Machine", "gcode_flavor": "{{gcodeFlavor}}"}""");
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.PressureAdvanceTower),
            CalibrationParamsJson = """{"start_advance": 0.0, "advance_step": 0.01, "band_height_mm": 5, "band_count": 4}""",
        };

        await OrcaSlicingPipelineService.ApplyPressureAdvanceTowerGcodeAsync(job, processJsonPath, machineJsonPath, CancellationToken.None);

        string updatedContent = await File.ReadAllTextAsync(processJsonPath);
        using JsonDocument doc = JsonDocument.Parse(updatedContent);
        string layerChangeGcode = doc.RootElement.GetProperty("layer_change_gcode").GetString()!;
        CalibrationFirmwareFlavor expectedFlavor = PressureAdvanceTowerGcodeBuilder.TryResolveFirmwareFlavor(gcodeFlavor)!.Value;
        string expectedGcode = PressureAdvanceTowerGcodeBuilder.BuildLayerChangeGcode(
            expectedFlavor,
            startAdvance: 0.0,
            advanceStep: 0.01,
            bandHeightMm: 5,
            bandCount: 4);
        layerChangeGcode.Should().Be(
            expectedGcode,
            "the pipeline must inject the exact gcode computed from the job's CalibrationParamsJson and the " +
            "machine profile's gcode_flavor, not a default/fallback template or the wrong firmware's command");
        job.ProcessProfileSha256.Should().NotBeNullOrEmpty();
        job.ProcessProfileSha256.Should().Be(
            NativeSlicerProfiles.ComputeSha256(updatedContent),
            "the recorded digest must match the mutated process profile content, not the original");
    }

    [Theory]
    [InlineData("reprap")]
    [InlineData("reprapfirmware")]
    [InlineData("repetier")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ApplyPressureAdvanceTowerGcodeAsync_UnsupportedFirmwareFlavor_ThrowsRatherThanSilentlyNoOp(string? gcodeFlavor)
    {
        // Acceptance criterion (issue #2136): an unsupported firmware flavour must be refused
        // explicitly, not silently produce a tower gcode that changes nothing. This is checked
        // before the process profile is ever mutated, and before OrcaSlicer runs.
        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        const string originalProcessJson = """{"name": "Test Process"}""";
        await File.WriteAllTextAsync(processJsonPath, originalProcessJson);
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        string machineJsonContent = gcodeFlavor is null
            ? """{"name": "Test Machine"}"""
            : $$"""{"name": "Test Machine", "gcode_flavor": "{{gcodeFlavor}}"}""";
        await File.WriteAllTextAsync(machineJsonPath, machineJsonContent);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.PressureAdvanceTower),
        };

        Func<Task> act = () => OrcaSlicingPipelineService.ApplyPressureAdvanceTowerGcodeAsync(
            job, processJsonPath, machineJsonPath, CancellationToken.None);

        var exceptionAssertions = await act.Should().ThrowAsync<InvalidOperationException>(
            "an unsupported or missing firmware flavour must be refused explicitly instead of " +
            "silently slicing a pressure advance tower that never changes the advance value");
        exceptionAssertions.Which.Message.Should().ContainAll("gcode_flavor", "Klipper", "Marlin");
        (await File.ReadAllTextAsync(processJsonPath)).Should().Be(
            originalProcessJson,
            "the process profile must not be mutated when the firmware flavour is refused");
        job.ProcessProfileSha256.Should().BeNull("no digest should be recorded for a job that was refused");
    }

    [Fact]
    public async Task ApplyPressureAdvanceTowerGcodeAsync_ExtremelyLongGcodeFlavor_TruncatesValueInExceptionMessage()
    {
        // Regression: gcode_flavor comes from an untrusted-ish machine profile blob. Echoing it
        // unbounded into the exception message (which can surface into job failure telemetry/logs)
        // would let an adversarial profile smuggle an arbitrarily large string into those logs.
        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(processJsonPath, """{"name": "Test Process"}""");
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        string hugeFlavor = new('x', 5000);
        await File.WriteAllTextAsync(
            machineJsonPath,
            JsonSerializer.Serialize(new { name = "Test Machine", gcode_flavor = hugeFlavor }));
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.PressureAdvanceTower),
        };

        Func<Task> act = () => OrcaSlicingPipelineService.ApplyPressureAdvanceTowerGcodeAsync(
            job, processJsonPath, machineJsonPath, CancellationToken.None);

        var exceptionAssertions = await act.Should().ThrowAsync<InvalidOperationException>();
        exceptionAssertions.Which.Message.Length.Should().BeLessThan(500);
        exceptionAssertions.Which.Message.Should().NotContain(hugeFlavor);
    }

    [Theory]
    [InlineData("Bambu Lab X1 Carbon 0.4 nozzle")]
    [InlineData("bambu lab a1 mini")] // case-insensitive match
    public async Task ApplyPressureAdvanceTowerGcodeAsync_BambuLabPrinterModel_ThrowsRatherThanSilentlyMisappliesMarlinCommand(string printerModel)
    {
        // Regression (Bishop review finding, re-review @ f2c1ae50d): upstream OrcaSlicer's Bambu
        // Lab (BBL) machine profiles inherit gcode_flavor: "marlin" from fdm_machine_common, so
        // without this check a BBL job would resolve to the ordinary Marlin branch and emit a
        // bare "M900 K{v}" -- but upstream's own GCodeWriter::set_pressure_advance branches on a
        // distinct is_bbl_printers flag *before* gcode_flavor and emits "M900 K{v} L1000 M10" for
        // BBL specifically. Silently treating BBL as generic Marlin would therefore slice a tower
        // with the wrong command for that hardware -- exactly the silent-mis-slice outcome this
        // calibration method must never produce. Must be refused explicitly instead.
        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        const string originalProcessJson = """{"name": "Test Process"}""";
        await File.WriteAllTextAsync(processJsonPath, originalProcessJson);
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            machineJsonPath,
            JsonSerializer.Serialize(new { name = "Test Machine", gcode_flavor = "marlin", printer_model = printerModel }));
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.PressureAdvanceTower),
        };

        Func<Task> act = () => OrcaSlicingPipelineService.ApplyPressureAdvanceTowerGcodeAsync(
            job, processJsonPath, machineJsonPath, CancellationToken.None);

        var exceptionAssertions = await act.Should().ThrowAsync<InvalidOperationException>(
            "a Bambu Lab machine profile must be refused explicitly instead of silently treated as generic Marlin");
        exceptionAssertions.Which.Message.Should().ContainAll("Bambu Lab", printerModel);
        (await File.ReadAllTextAsync(processJsonPath)).Should().Be(
            originalProcessJson,
            "the process profile must not be mutated when a Bambu Lab machine is refused");
        job.ProcessProfileSha256.Should().BeNull("no digest should be recorded for a job that was refused");
    }

    [Theory]
    [InlineData("Bambu Lab X1 Carbon 0.4 nozzle", true)]
    [InlineData("BAMBU LAB P1S", true)]
    [InlineData("Prusa MK4S", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsBambuLabPrinterModel_IdentifiesBambuLabModelsOnly(string? printerModel, bool expected)
    {
        PressureAdvanceTowerGcodeBuilder.IsBambuLabPrinterModel(printerModel).Should().Be(expected);
    }

    [Fact]
    public async Task ApplyPressureAdvanceTowerGcodeAsync_KlipperFlashedBambuLabPrinterModel_IsAcceptedNotFalselyRefused()
    {
        // Regression (Bishop round-3 non-blocking note): a Klipper-flashed Bambu Lab machine is a
        // real, supported configuration -- its gcode_flavor already resolves to Klipper, so
        // SET_PRESSURE_ADVANCE is the correct command for it. The BBL refusal above exists only to
        // stop a *Marlin-flavoured* BBL profile (upstream's own dialect mismatch) from silently
        // mis-slicing; it must never over-refuse a machine whose flavour already resolved to
        // Klipper just because its printer_model string happens to say "Bambu Lab".
        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(processJsonPath, """{"name": "Test Process"}""");
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            machineJsonPath,
            JsonSerializer.Serialize(new { name = "Test Machine", gcode_flavor = "klipper", printer_model = "Bambu Lab X1 Carbon 0.4 nozzle (Klipper)" }));
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.PressureAdvanceTower),
            CalibrationParamsJson = """{"start_advance": 0.02, "advance_step": 0.01, "band_height_mm": 5, "band_count": 2}""",
        };

        await OrcaSlicingPipelineService.ApplyPressureAdvanceTowerGcodeAsync(job, processJsonPath, machineJsonPath, CancellationToken.None);

        string updatedProcessContent = await File.ReadAllTextAsync(processJsonPath);
        using JsonDocument doc = JsonDocument.Parse(updatedProcessContent);
        string layerChangeGcode = doc.RootElement.GetProperty("layer_change_gcode").GetString()!;
        layerChangeGcode.Should().Contain(
            "SET_PRESSURE_ADVANCE",
            "a Klipper-flashed Bambu Lab machine must resolve to the Klipper command, not be refused as BBL");
        job.ProcessProfileSha256.Should().NotBeNull("a successfully processed job must record its updated process profile digest");
    }

    [Fact]
    public async Task ApplyRetractionTowerGcodeAsync_InjectsLayerChangeGcodeAndForcesFirmwareRetractionAndRecomputesDigests()
    {
        // Mirrors ApplyTemperatureTowerGcodeAsync_InjectsLayerChangeGcodeAndRecomputesDigest: a
        // unit test on RetractionTowerGcodeBuilder alone does not prove the gcode reaches the
        // process profile on disk, that use_firmware_retraction is forced on the machine profile
        // (without which the injected M207 has no physical effect — see
        // RetractionTowerGcodeBuilder's remarks), or that both recorded digests are recomputed to
        // match.
        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(processJsonPath, """{"name": "Test Process"}""");
        // Mirrors a real vendor machine profile (BambuLab/Prusa/Creality/Voron all ship wipe
        // enabled): use_firmware_retraction=1 combined with any extruder's wipe=1 is a hard
        // config-validation error in OrcaSlicer, so the pipeline must also force wipe off.
        await File.WriteAllTextAsync(machineJsonPath, """{"name": "Test Machine", "gcode_flavor": "marlin", "use_firmware_retraction": "0", "wipe": ["1"]}""");
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.Retraction),
            CalibrationParamsJson = """{"start_retraction_mm": 0.3, "retraction_step_mm": 0.3, "retraction_band_height_mm": 8, "retraction_band_count": 4}""",
        };

        await OrcaSlicingPipelineService.ApplyRetractionTowerGcodeAsync(job, processJsonPath, machineJsonPath, CancellationToken.None);

        string updatedProcessContent = await File.ReadAllTextAsync(processJsonPath);
        using JsonDocument processDoc = JsonDocument.Parse(updatedProcessContent);
        string layerChangeGcode = processDoc.RootElement.GetProperty("layer_change_gcode").GetString()!;
        string expectedGcode = RetractionTowerGcodeBuilder.BuildLayerChangeGcode(
            CalibrationFirmwareFlavor.Marlin,
            startRetractionMm: 0.3,
            retractionStepMm: 0.3,
            bandHeightMm: 8,
            bandCount: 4);
        layerChangeGcode.Should().Be(
            expectedGcode,
            "the pipeline must inject the exact gcode computed from the job's CalibrationParamsJson, " +
            "not a default/fallback template");
        job.ProcessProfileSha256.Should().NotBeNullOrEmpty();
        job.ProcessProfileSha256.Should().Be(
            NativeSlicerProfiles.ComputeSha256(updatedProcessContent),
            "the recorded digest must match the mutated process profile content, not the original");

        string updatedMachineContent = await File.ReadAllTextAsync(machineJsonPath);
        using JsonDocument machineDoc = JsonDocument.Parse(updatedMachineContent);
        machineDoc.RootElement.GetProperty("use_firmware_retraction").GetString().Should().Be(
            "1",
            "M207-driven retraction only takes effect when firmware retraction is enabled on the printer profile");
        machineDoc.RootElement.GetProperty("wipe").EnumerateArray().Select(e => e.GetString()).Should().AllBe(
            "0",
            "OrcaSlicer's config validator hard-rejects use_firmware_retraction=1 combined with any extruder's wipe=1");
        job.MachineProfileSha256.Should().NotBeNullOrEmpty();
        job.MachineProfileSha256.Should().Be(
            NativeSlicerProfiles.ComputeSha256(updatedMachineContent),
            "the recorded digest must match the mutated machine profile content, not the original");
    }

    [Fact]
    public async Task ApplyRetractionTowerGcodeAsync_KlipperMachine_EmitsSetRetractionInsteadOfM207()
    {
        // Issue #2137 review finding: a Klipper machine profile must never receive Marlin's M207
        // -- Klipper does not recognize it at all, so the tower would silently print with no
        // retraction sweep whatsoever while still reporting success.
        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(processJsonPath, """{"name": "Test Process"}""");
        await File.WriteAllTextAsync(machineJsonPath, """{"name": "Test Machine", "gcode_flavor": "klipper", "use_firmware_retraction": "0"}""");
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.Retraction),
            CalibrationParamsJson = """{"start_retraction_mm": 0.3, "retraction_step_mm": 0.3, "retraction_band_height_mm": 8, "retraction_band_count": 4}""",
        };

        await OrcaSlicingPipelineService.ApplyRetractionTowerGcodeAsync(job, processJsonPath, machineJsonPath, CancellationToken.None);

        string updatedProcessContent = await File.ReadAllTextAsync(processJsonPath);
        using JsonDocument processDoc = JsonDocument.Parse(updatedProcessContent);
        string layerChangeGcode = processDoc.RootElement.GetProperty("layer_change_gcode").GetString()!;
        layerChangeGcode.Should().Contain("SET_RETRACTION", "Klipper machines must receive Klipper's own retraction-length command");
        layerChangeGcode.Should().NotContain("M207", "M207 is Marlin-only and is silently ignored by Klipper");
        job.ProcessProfileSha256.Should().NotBeNull("a successfully processed job must record its updated process profile digest");
    }

    [Fact]
    public async Task ApplyRetractionTowerGcodeAsync_UnsupportedFirmwareFlavor_ThrowsRatherThanSilentlyEmittingM207()
    {
        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(processJsonPath, """{"name": "Test Process"}""");
        await File.WriteAllTextAsync(machineJsonPath, """{"name": "Test Machine", "gcode_flavor": "reprap"}""");
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.Retraction),
            CalibrationParamsJson = """{"start_retraction_mm": 0.3, "retraction_step_mm": 0.3, "retraction_band_height_mm": 8, "retraction_band_count": 4}""",
        };

        Func<Task> act = () => OrcaSlicingPipelineService.ApplyRetractionTowerGcodeAsync(job, processJsonPath, machineJsonPath, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        job.ProcessProfileSha256.Should().BeNull("a refused job must never record a digest for content it never wrote");
    }

    [Fact]
    public async Task ApplyRetractionTowerGcodeAsync_MissingGcodeFlavor_ThrowsRatherThanSilentlyEmittingM207()
    {
        string processJsonPath = Path.Combine(_tempDir, $"process-{Guid.NewGuid():N}.json");
        string machineJsonPath = Path.Combine(_tempDir, $"machine-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(processJsonPath, """{"name": "Test Process"}""");
        await File.WriteAllTextAsync(machineJsonPath, """{"name": "Test Machine"}""");
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.Retraction),
            CalibrationParamsJson = """{"start_retraction_mm": 0.3, "retraction_step_mm": 0.3, "retraction_band_height_mm": 8, "retraction_band_count": 4}""",
        };

        Func<Task> act = () => OrcaSlicingPipelineService.ApplyRetractionTowerGcodeAsync(job, processJsonPath, machineJsonPath, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void EnableFirmwareRetraction_TurnsOnFirmwareRetractionWithoutDisturbingOtherKeys()
    {
        string machineJson = """{"name": "Test Machine", "bed_shape": ["0x0", "250x0", "250x250", "0x250"], "use_firmware_retraction": "0"}""";

        string updated = OrcaSlicingPipelineService.EnableFirmwareRetraction(machineJson);

        using JsonDocument doc = JsonDocument.Parse(updated);
        doc.RootElement.GetProperty("use_firmware_retraction").GetString().Should().Be("1");
        doc.RootElement.GetProperty("name").GetString().Should().Be("Test Machine");
        // No "wipe" key was present at all: rather than rely on upstream resolving the missing
        // key to its documented false default, an explicit single-extruder "off" array is
        // written so this is defensible even if that upstream default ever changes.
        doc.RootElement.GetProperty("wipe").EnumerateArray().Select(e => e.GetString()).Should().Equal("0");
    }

    [Fact]
    public void EnableFirmwareRetraction_MultiExtruderWipeEnabled_ForcesEveryExtruderOffPreservingArrayLength()
    {
        // Real multi-extruder vendor profiles (e.g. an IDEX or multi-toolhead machine) carry one
        // wipe entry per extruder. Forcing wipe off must preserve that length rather than
        // collapsing it to a single entry, which would desync it from other per-extruder arrays
        // (nozzle_diameter, extruder_colour, ...) the same profile carries.
        string machineJson = """{"name": "Dual Extruder Machine", "wipe": ["1", "1"]}""";

        string updated = OrcaSlicingPipelineService.EnableFirmwareRetraction(machineJson);

        using JsonDocument doc = JsonDocument.Parse(updated);
        doc.RootElement.GetProperty("use_firmware_retraction").GetString().Should().Be("1");
        doc.RootElement.GetProperty("wipe").EnumerateArray().Select(e => e.GetString()).Should().Equal("0", "0");
    }

    [Fact]
    public void PrepareCalibrationModel_MaxVolumetricSpeedMethod_CopiesResourceUnmodified()
    {
        // Issue #2135: SpeedTestStructure.drc is an opaque OrcaSlicer binary format (confirmed by
        // magic bytes against a local install, not a ZIP/3MF archive), so — like the temperature
        // tower's .drc resource — the worker must copy it unmodified rather than attempt to parse
        // and rewrite it.
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-mvs");
        string mvsPath = Path.Combine(calibResourcesRoot, "volumetric_speed", "SpeedTestStructure.drc");
        Directory.CreateDirectory(Path.GetDirectoryName(mvsPath)!);
        File.WriteAllText(mvsPath, "fake-mvs-resource");

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.MaximumVolumetricSpeed),
        };

        string preparedPath = pipeline.PrepareCalibrationModel(job, workDir);

        File.Exists(preparedPath).Should().BeTrue();
        File.ReadAllText(preparedPath).Should().Be("fake-mvs-resource");
    }

    [Theory]
    [InlineData("klipper")]
    [InlineData("marlin")]
    [InlineData("KLIPPER")]
    public void PrepareCalibrationModel_InputShapingMethod_SupportedFirmwareFlavor_CopiesResourceUnmodified(string firmwareFlavor)
    {
        // Issue #2139: ringing_tower.drc is an opaque OrcaSlicer binary format (confirmed by magic
        // bytes against a local install, not a ZIP/3MF archive), so — like the temperature
        // tower's and max volumetric speed's .drc resources — the worker must copy it unmodified
        // rather than attempt to parse and rewrite it. Report-only: no per-object rewriting, no
        // profile injection, just the resource.
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-is-" + Guid.NewGuid().ToString("N"));
        string ringingTowerPath = Path.Combine(calibResourcesRoot, "input_shaping", "ringing_tower.drc");
        Directory.CreateDirectory(Path.GetDirectoryName(ringingTowerPath)!);
        File.WriteAllText(ringingTowerPath, "fake-ringing-tower-resource");

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.InputShaping),
            CalibrationParamsJson = $$"""{"firmware_flavor": "{{firmwareFlavor}}"}""",
        };

        string preparedPath = pipeline.PrepareCalibrationModel(job, workDir);

        File.Exists(preparedPath).Should().BeTrue();
        File.ReadAllText(preparedPath).Should().Be("fake-ringing-tower-resource");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{}""")]
    [InlineData("""{"firmware_flavor": ""}""")]
    [InlineData("""{"firmware_flavor": "reprap"}""")]
    [InlineData("""{"firmware_flavor": "smoothieware"}""")]
    public void PrepareCalibrationModel_InputShapingMethod_UnsupportedOrMissingFirmwareFlavor_ThrowsExplicitRefusal(
        string? calibrationParamsJson)
    {
        // Issue #2139: input shaping is firmware-specific — the operator must apply the result
        // (frequency/damping-factor) to their own firmware config, and Klipper's [input_shaper]
        // vs. Marlin's M593 need different guidance. A missing or unrecognized firmware flavor
        // must fail loudly here, before the worker slices a tower the operator has no
        // firmware-specific guidance to act on — not silently default to either flavor and not
        // silently no-op.
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-is-refuse-" + Guid.NewGuid().ToString("N"));
        string ringingTowerPath = Path.Combine(calibResourcesRoot, "input_shaping", "ringing_tower.drc");
        Directory.CreateDirectory(Path.GetDirectoryName(ringingTowerPath)!);
        File.WriteAllText(ringingTowerPath, "fake-ringing-tower-resource");

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.InputShaping),
            CalibrationParamsJson = calibrationParamsJson,
        };

        Action act = () => pipeline.PrepareCalibrationModel(job, workDir);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(
                "firmware flavor",
                "the message must explain that a supported firmware flavor is required, not just that " +
                "it failed with some InvalidOperationException")
            .And.NotContain(
                calibResourcesRoot,
                "the exception message must not disclose internal worker filesystem paths");
    }

    [Fact]
    public async Task ApplyMaxVolumetricSpeedCeilingAsync_SetsCeilingAndRecomputesDigest()
    {
        // Regression coverage for the pipeline's filament-profile injection wiring: a unit test on
        // the JSON helper alone does not prove the ceiling actually reaches the filament profile
        // on disk, or that the recorded digest is recomputed to match.
        string filamentJsonPath = Path.Combine(_tempDir, $"filament-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(filamentJsonPath, """{"name": "Test Filament"}""");
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.MaximumVolumetricSpeed),
            CalibrationParamsJson = """{"max_volumetric_speed_ceiling_mm3s": 35}""",
        };

        await OrcaSlicingPipelineService.ApplyMaxVolumetricSpeedCeilingAsync(job, filamentJsonPath, CancellationToken.None);

        string updatedContent = await File.ReadAllTextAsync(filamentJsonPath);
        using JsonDocument doc = JsonDocument.Parse(updatedContent);
        JsonElement ceilingElement = doc.RootElement.GetProperty("filament_max_volumetric_speed");
        ceilingElement.ValueKind.Should().Be(JsonValueKind.Array, "OrcaSlicer stores filament settings as single-element arrays");
        ceilingElement[0].GetString().Should().Be(
            "35",
            "the pipeline must inject the exact ceiling computed from the job's CalibrationParamsJson, " +
            "not the 50mm³/s default — a bug that ignores the client-supplied override would otherwise " +
            "go undetected because the default also happens to be a plausible ceiling");
        job.FilamentProfileSha256.Should().NotBeNullOrEmpty();
        job.FilamentProfileSha256.Should().Be(
            NativeSlicerProfiles.ComputeSha256(updatedContent),
            "the recorded digest must match the mutated filament profile content, not the original");
    }

    [Fact]
    public async Task ApplyMaxVolumetricSpeedCeilingAsync_MultiExtruder_SetsCeilingOnEveryFilamentAndRecomputesSetDigest()
    {
        // Regression coverage for a multi-extruder job: GenerateProfileJsonFilesAsync joins
        // per-extruder filament paths with ';' (matching the --load-filaments CLI argument shape)
        // when profile.ExtruderFilamentProfiles has more than one entry, and RunOrcaSlicerAsync
        // passes that joined string straight through as profilePaths["filament"]. Before this fix,
        // ApplyMaxVolumetricSpeedCeilingAsync treated the joined string as a single path and threw
        // FileNotFoundException for any multi-extruder MVS calibration job.
        string filamentPathA = Path.Combine(_tempDir, $"filament-a-{Guid.NewGuid():N}.json");
        string filamentPathB = Path.Combine(_tempDir, $"filament-b-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(filamentPathA, """{"name": "Filament A"}""");
        await File.WriteAllTextAsync(filamentPathB, """{"name": "Filament B", "filament_flow_ratio": ["0.97"]}""");
        string joinedFilamentPath = string.Join(';', filamentPathA, filamentPathB);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(CalibrationMethod.MaximumVolumetricSpeed),
            CalibrationParamsJson = """{"max_volumetric_speed_ceiling_mm3s": 42}""",
        };

        await OrcaSlicingPipelineService.ApplyMaxVolumetricSpeedCeilingAsync(job, joinedFilamentPath, CancellationToken.None);

        string updatedA = await File.ReadAllTextAsync(filamentPathA);
        string updatedB = await File.ReadAllTextAsync(filamentPathB);
        using JsonDocument docA = JsonDocument.Parse(updatedA);
        using JsonDocument docB = JsonDocument.Parse(updatedB);
        docA.RootElement.GetProperty("filament_max_volumetric_speed")[0].GetString().Should().Be("42");
        docB.RootElement.GetProperty("filament_max_volumetric_speed")[0].GetString().Should().Be("42");
        docB.RootElement.GetProperty("filament_flow_ratio")[0].GetString().Should().Be(
            "0.97", "injecting the ceiling must not clobber a filament's other existing keys");

        job.FilamentProfileSha256.Should().Be(
            NativeSlicerProfiles.ComputeSha256(string.Join('\0', updatedA, updatedB)),
            "the multi-filament digest must follow the same \\0-joined-set convention " +
            "GenerateProfileJsonFilesAsync uses (ComputeProfileSetSha256), not a hash of a single document");
    }

    [Fact]
    public void InjectMaxVolumetricSpeedCeiling_PreservesExistingKeys()
    {
        const string filamentJson = """{"name": "Test Filament", "filament_flow_ratio": ["0.98"]}""";

        string updated = OrcaSlicingPipelineService.InjectMaxVolumetricSpeedCeiling(filamentJson, 42.5);

        using JsonDocument doc = JsonDocument.Parse(updated);
        doc.RootElement.GetProperty("name").GetString().Should().Be("Test Filament");
        doc.RootElement.GetProperty("filament_flow_ratio")[0].GetString().Should().Be("0.98");
        doc.RootElement.GetProperty("filament_max_volumetric_speed")[0].GetString().Should().Be("42.5");
    }

    private static OrcaSlicingPipelineService CreatePipeline(string calibResourcesRoot)
    {
        IConfiguration configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Worker:WorkingDirectory"] = Path.Combine(Path.GetTempPath(), $"orca-worker-{Guid.NewGuid():N}"),
                    ["SlicerApi:BaseUrl"] = "http://localhost",
                    ["Worker:CalibrationResourcesPath"] = calibResourcesRoot,
                })
                .Build();
        return new OrcaSlicingPipelineService(
            new HttpClient(),
            new NullProgressReporter(),
            NullLogger<OrcaSlicingPipelineService>.Instance,
            configuration,
            new WorkerStateService());
    }

    private void CreateSynthetic3mfAt(string path, (string Id, string Name)[] objects)
    {
        using FileStream fs = File.Create(path);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry("3D/3dmodel.model");
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(BuildModelXml(objects));
    }

    #endregion

    private static string BuildModelXml(params (string Id, string Name)[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8"?><model xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02"><resources>""");
        foreach ((string id, string name) in objects)
        {
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"""<object id="{id}" name="{name}" type="model"><mesh><vertices/><triangles/></mesh></object>""");
        }

        sb.Append("</resources><build/></model>");
        return sb.ToString();
    }

    private string CreateSynthetic3mf((string Id, string Name)[] objects)
    {
        // The source resource file and the worker's destination work directory are always
        // distinct in production; mirror that here so File.Copy never collides with itself.
        string sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);
        string path = Path.Join(sourceDir, $"source-{Guid.NewGuid():N}.3mf");
        using (FileStream fs = File.Create(path))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("3D/3dmodel.model");
            using Stream stream = entry.Open();
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(BuildModelXml(objects));
        }

        return path;
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class NullProgressReporter : IProgressReporter
    {
        public Task ReportProgressAsync(
            Guid jobId,
            Guid claimToken,
            int progress,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReportCompletionAsync(
            DistributedSlicingJob job,
            SlicingResult result,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReportFailureAsync(
            Guid jobId,
            Guid claimToken,
            string errorMessage,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
