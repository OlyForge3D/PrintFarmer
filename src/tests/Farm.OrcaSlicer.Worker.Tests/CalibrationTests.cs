using System.IO.Compression;
using System.Text;
using Farm.OrcaSlicer.Worker.Services.Calibration;
using Farm.Slicer.Module.Models;
using FluentAssertions;
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
        // supported yet, and must fail clearly rather than silently degrading into a generic
        // slice failure.
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
    public void ApplyPerObjectFlowRatios_NoParseableNames_CopiesModelWithoutOverrides()
    {
        string source3mf = CreateSynthetic3mf([("1", "Body")]);

        string resultPath = FlowRateCalibrationConfigurator.ApplyPerObjectFlowRatios(
            source3mf,
            _tempDir,
            NullLogger.Instance);

        File.Exists(resultPath).Should().BeTrue();
        using ZipArchive archive = ZipFile.OpenRead(resultPath);
        archive.GetEntry("Metadata/Slic3r_PE_model.config").Should().BeNull();
    }

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

    #endregion
}
