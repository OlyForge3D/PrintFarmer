using System.Text.RegularExpressions;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services.Calibration.Generation;

/// <summary>
/// Compares PrintFarmer's generated Temperature-method output against a checked-in reference
/// derived from OrcaSlicer's own documented temperature-tower calibration mechanism.
/// </summary>
/// <remarks>
/// <para>
/// Pinned reference version: <b>OrcaSlicer v2.4.2</b>
/// (commit <c>8500fcdccaa10b5099ac20d252af3a7c560046f1</c>), recorded in
/// <c>compliance/calibration-provenance.json</c> under
/// <c>approvedSources[id=orcaslicer-v2.4.2]</c> and
/// <c>referenceRecords[id=calibration-temperature-tower-orcaslicer-golden-fixture]</c>.
/// </para>
/// <para>
/// No OrcaSlicer binary or GUI execution path is available in this environment (no vendored
/// engine, no headless slicing entry point -- see
/// <c>src/Slicers/Farm.Slicers.OrcaSlicer.v2_4_0</c> and <c>v2_3_1</c>, which are metadata/UI
/// wrapper projects only). The checked-in reference file
/// (<c>Fixtures/orcaslicer-temperature-tower-v2.4.2.gcode</c>) is therefore not a byte-for-byte
/// capture of a live OrcaSlicer slice. It is a faithful reconstruction of the one part of
/// OrcaSlicer's temperature-tower mechanism that is documented and stable: the ordered per-band
/// nozzle temperature setpoint commands (<c>M104</c>/<c>M109</c>) OrcaSlicer issues before each
/// tower band. This test asserts only that ordered setpoint sequence; it does not verify full
/// tower toolpath/geometry parity, which would require running the real OrcaSlicer engine. This
/// gap is intentional and is flagged for reviewer awareness -- see
/// https://github.com/OlyForge3D/PrintFarmer/issues/1926 and
/// https://github.com/OlyForge3D/PrintFarmer/issues/1929.
/// </para>
/// </remarks>
public sealed class CalibrationTemperatureTowerOrcaGoldenTests
{
    private static readonly Regex SetpointLine = new(
        @"^M10[49] S-?\d+$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static string FixturePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",
            "Services", "Calibration", "Generation", "Fixtures",
            "orcaslicer-temperature-tower-v2.4.2.gcode");

    [Fact]
    public void Generate_TemperatureTower_MatchesOrcaSlicerDocumentedSetpointSequence()
    {
        CalibrationGenerationPipeline.Result run =
            CalibrationGenerationPipeline.Run(CalibrationMethod.Temperature, 0.4m, directDrive: true);

        _ = run.Problems.Should().BeEmpty();

        IReadOnlyList<string> actualSetpoints = ExtractSegmentSetpoints(run.Annotated!.Gcode);
        IReadOnlyList<string> referenceSetpoints = ExtractSetpointsFromFixture();

        _ = actualSetpoints.Should().Equal(
            referenceSetpoints,
            "PrintFarmer's per-band temperature setpoints must not diverge from the pinned " +
            "OrcaSlicer v2.4.2 temperature-tower reference recorded in " +
            "compliance/calibration-provenance.json " +
            "(referenceRecords[id=calibration-temperature-tower-orcaslicer-golden-fixture])");
    }

    [Fact]
    public void ReferenceFixture_IsPresentAndNonEmpty()
    {
        string path = FixturePath;
        File.Exists(path).Should().BeTrue(
            $"the checked-in OrcaSlicer-derived reference must exist at '{path}'");

        IReadOnlyList<string> referenceSetpoints = ExtractSetpointsFromFixture();
        referenceSetpoints.Should().NotBeEmpty()
            .And.HaveCount(18, "the fixture pins exactly nine descending temperature bands");
    }

    /// <summary>
    /// Extracts the ordered <c>M104</c>/<c>M109</c> setpoint commands PrintFarmer emits inside
    /// each <c>;PF_SEG_BEGIN ... nozzle_temperature ...</c> block, skipping the unrelated
    /// initialization-block M104/M109 pair that primes the first band's temperature before the
    /// tower begins.
    /// </summary>
    private static IReadOnlyList<string> ExtractSegmentSetpoints(string gcode)
    {
        string normalized = gcode.Replace("\r\n", "\n");
        string[] lines = normalized.Split('\n');
        List<string> setpoints = [];

        for (int index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith(CalibrationGcodeMarkers.SegmentBegin, StringComparison.Ordinal)
                || !lines[index].Contains(
                    $"PARAM={CalibrationSweepResolver.NozzleTemperatureParameter}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            for (int cursor = index + 1;
                cursor < lines.Length
                    && !lines[cursor].StartsWith(CalibrationGcodeMarkers.SegmentEnd, StringComparison.Ordinal);
                cursor++)
            {
                if (SetpointLine.IsMatch(lines[cursor]))
                {
                    setpoints.Add(lines[cursor]);
                }
            }
        }

        return setpoints;
    }

    private static IReadOnlyList<string> ExtractSetpointsFromFixture()
    {
        string text = File.ReadAllText(FixturePath).Replace("\r\n", "\n");
        return [.. SetpointLine.Matches(text).Select(match => match.Value)];
    }
}
