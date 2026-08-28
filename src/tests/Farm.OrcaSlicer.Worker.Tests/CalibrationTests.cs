using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using Farm.OrcaSlicer.Worker.Services.Calibration;
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
    [InlineData("retraction")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_UnsupportedOrMissingWireName_ReturnsFalse(string? wireName)
    {
        // PA Pattern (GPL-3.0 provenance) and PA Line (Bambu-specific) are deliberately not
        // supported yet, and retraction is simply not yet built (issue #2051 investigation) —
        // all must fail clearly rather than silently degrading into a generic slice failure.
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
    public void ClientAcceptedWireNames_ExcludesYoloMethodsButIncludesEverythingElse()
    {
        // Issue #2051: the two YOLO methods parse (they are catalogued) but are not yet
        // slicer-supported, so they must not appear in the list a controller advertises to
        // clients as "supported methods" — otherwise the API would recommend a method it
        // immediately rejects.
        CalibrationMethods.ClientAcceptedWireNames.Should()
            .NotContain("flow_rate_yolo_recommended")
            .And.NotContain("flow_rate_yolo_perfectionist");

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
            .HaveCount(CalibrationMethods.SupportedWireNames.Count - 2);
    }

    [Theory]
    [InlineData(CalibrationMethod.FlowRateYoloRecommended, "Orca-LinearFlow.3mf")]
    [InlineData(CalibrationMethod.FlowRateYoloPerfectionist, "Orca-LinearFlow_fine.3mf")]
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
    public void CalibrationParameters_Parse_MixedNumericAndFirmwareFlavor_BothParseIndependently()
    {
        // A single CalibrationParamsJson payload can now legally mix numeric keys (used by other
        // methods) with the string firmware_flavor key (issue #2139) — confirms the per-key
        // JsonDocument-based parse doesn't regress numeric parsing when a string value is present
        // elsewhere in the same object, unlike the old whole-payload Dictionary<string,double>
        // deserialization, which would have thrown and fallen back to all defaults.
        CalibrationParameters parameters = CalibrationParameters.Parse(
            """{"start_temperature": 220, "firmware_flavor": "marlin"}""",
            CalibrationMethod.InputShaping);

        parameters.FirmwareFlavor.Should().Be("marlin");
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
        // "NaN" as a JSON string fails Dictionary<string,double> deserialization entirely and
        // falls back to defaults via the malformed-JSON path; numeric out-of-range values fall
        // back via the bounds check. Either way the result must be the safe default.
        CalibrationParameters parameters = CalibrationParameters.Parse(json, CalibrationMethod.TemperatureTower);

        parameters.StartTemperatureC.Should().Be(230);
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

    [Theory]
    [InlineData(CalibrationMethod.FlowRateYoloRecommended, "Orca-LinearFlow.3mf")]
    [InlineData(CalibrationMethod.FlowRateYoloPerfectionist, "Orca-LinearFlow_fine.3mf")]
    public void PrepareCalibrationModel_YoloMethod_ThrowsBecauseDeltaOverridesAreNotYetSupported(
        CalibrationMethod method,
        string resourceFileName)
    {
        // The YOLO resources' per-object names encode baseline-relative deltas
        // (e.g. "flowrate_0.01"), not the absolute percentages FlowRateCalibrationConfigurator
        // parses for pass1/pass2. The worker must fail loudly here instead of silently copying
        // the resource unmodified (which would slice an uncalibrated result) or misapplying the
        // pass1/2 parser (see CalibrationMethod.cs remarks, issue #2051).
        string calibResourcesRoot = Path.Combine(_tempDir, "calib-resources-yolo-" + Guid.NewGuid().ToString("N"));
        string resourcePath = Path.Combine(calibResourcesRoot, "filament_flow", resourceFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(resourcePath)!);
        CreateSynthetic3mfAt(resourcePath, [("1", "flowrate_0.01"), ("2", "flowrate_m0.01")]);

        OrcaSlicingPipelineService pipeline = CreatePipeline(calibResourcesRoot);
        string workDir = Path.Combine(_tempDir, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var job = new DistributedSlicingJob
        {
            CalibrationMethod = CalibrationMethods.ToWireName(method),
        };

        Action act = () => pipeline.PrepareCalibrationModel(job, workDir);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(
                "not yet slicer-supported",
                "the message must explain *why* the job failed (delta-based overrides are not yet " +
                "supported), not just that it failed with some InvalidOperationException — a caller " +
                "cannot otherwise distinguish this from an unrelated internal error")
            .And.NotContain(
                calibResourcesRoot,
                "the exception message must not disclose internal worker filesystem paths");
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
