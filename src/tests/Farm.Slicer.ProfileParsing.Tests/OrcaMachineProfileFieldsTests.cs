using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Farm.Slicer.ProfileParsing.Tests;

/// <summary>
/// Unit tests for <see cref="OrcaMachineProfileFields"/>, lifted verbatim from PR-1's (#1614)
/// <c>CalibrationMachineProfileDeriver</c> and now shared between <c>orcaslicer-worker</c> and
/// the producer-side <c>CalibrationProfileResolver</c> (#1615 PR-2). These cases were originally
/// exercised against <c>CalibrationMachineProfileDeriver.Derive(string?)</c> before the deriver
/// became a typed-field passthrough; they now target the shared parser directly.
/// </summary>
public sealed class OrcaMachineProfileFieldsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParsePrintableAreaPoints_WithMissingProperty_ReturnsNull()
    {
        List<(double X, double Y)>? result = OrcaMachineProfileFields.ParsePrintableAreaPoints(
            Parse("{}"));

        _ = result.Should().BeNull();
    }

    [Fact]
    public void ParsePrintableAreaPoints_WithMalformedPointStrings_IgnoresUnparsablePoints()
    {
        List<(double X, double Y)>? result = OrcaMachineProfileFields.ParsePrintableAreaPoints(
            Parse("""{"printable_area": ["not-a-point", "also,bad", "250"]}"""));

        _ = result.Should().BeNull();
    }

    [Fact]
    public void ParsePrintableAreaPoints_WithEmptyArray_ReturnsNull()
    {
        List<(double X, double Y)>? result = OrcaMachineProfileFields.ParsePrintableAreaPoints(
            Parse("""{"printable_area": []}"""));

        _ = result.Should().BeNull();
    }

    [Fact]
    public void ParsePrintableAreaPoints_WithArrayOfPointStrings_ParsesAllPoints()
    {
        List<(double X, double Y)>? result = OrcaMachineProfileFields.ParsePrintableAreaPoints(
            Parse("""{"printable_area": ["0x0", "250x0", "250x250", "0x250"]}"""));

        _ = result.Should().BeEquivalentTo(new[] { (0d, 0d), (250d, 0d), (250d, 250d), (0d, 250d) });
    }

    [Fact]
    public void ParsePrintableAreaPoints_WithCommaJoinedString_ParsesAllPoints()
    {
        // The OrcaSlicer worker's real profile fixtures use this single comma-joined string
        // form rather than a JSON array (#1613 §5) — both must parse identically.
        List<(double X, double Y)>? result = OrcaMachineProfileFields.ParsePrintableAreaPoints(
            Parse("""{"printable_area": "0x0,220x0,220x220,0x220"}"""));

        _ = result.Should().BeEquivalentTo(new[] { (0d, 0d), (220d, 0d), (220d, 220d), (0d, 220d) });
    }

    [Fact]
    public void TryGetProperty_IsCaseInsensitive()
    {
        bool found = OrcaMachineProfileFields.TryGetProperty(
            Parse("""{"Printable_Area": "0x0,1x1"}"""), "printable_area", out JsonElement value);

        _ = found.Should().BeTrue();
        _ = value.ValueKind.Should().Be(JsonValueKind.String);
    }

    [Theory]
    [InlineData("not-a-real-nozzle-type")]
    [InlineData("")]
    public void ParseNozzleTypeRaw_ReturnsRawStringWithoutMapping(string rawValue)
    {
        string? result = OrcaMachineProfileFields.ParseNozzleTypeRaw(
            Parse($$"""{"nozzle_type": "{{rawValue}}"}"""));

        _ = result.Should().Be(rawValue);
    }

    [Fact]
    public void ParseNozzleTypeRaw_WithMissingProperty_ReturnsNull()
    {
        string? result = OrcaMachineProfileFields.ParseNozzleTypeRaw(Parse("{}"));

        _ = result.Should().BeNull();
    }

    [Fact]
    public void ParseMotionTypeRaw_FallsBackFromPrinterTypeToMachineType()
    {
        string? result = OrcaMachineProfileFields.ParseMotionTypeRaw(
            Parse("""{"machine_type": "corexy"}"""));

        _ = result.Should().Be("corexy");
    }

    [Fact]
    public void ParseMaxAccelerationX_WithNonNumericValue_ReturnsNull()
    {
        int? result = OrcaMachineProfileFields.ParseMaxAccelerationX(
            Parse("""{"machine_max_acceleration_x": ["not-a-number"]}"""));

        _ = result.Should().BeNull();
    }

    [Fact]
    public void ParseMaxAccelerationX_WithEmptyArray_ReturnsNull()
    {
        int? result = OrcaMachineProfileFields.ParseMaxAccelerationX(
            Parse("""{"machine_max_acceleration_x": []}"""));

        _ = result.Should().BeNull();
    }

    [Fact]
    public void ParseMaxHotendTemperature_FallsBackToNozzleTemperatureRangeHigh()
    {
        int? result = OrcaMachineProfileFields.ParseMaxHotendTemperature(
            Parse("""{"nozzle_temperature_range_high": [300]}"""));

        _ = result.Should().Be(300);
    }

    [Fact]
    public void AllFields_WithFullySpecifiedProfile_ParseCorrectly()
    {
        JsonElement root = Parse(
            """
            {
                "printable_area": ["0x0", "250x0", "250x250", "0x250"],
                "printable_height": 250,
                "machine_max_acceleration_x": [10000],
                "machine_max_speed_x": [500],
                "has_heated_bed": true,
                "has_heated_chamber": false,
                "nozzle_diameter": [0.4],
                "nozzle_type": "brass",
                "max_hotend_temp": [300],
                "printer_type": "corexy"
            }
            """);

        _ = OrcaMachineProfileFields.ParsePrintableAreaPoints(root).Should().HaveCount(4);
        _ = OrcaMachineProfileFields.ParsePrintableHeight(root).Should().Be(250);
        _ = OrcaMachineProfileFields.ParseMaxAccelerationX(root).Should().Be(10000);
        _ = OrcaMachineProfileFields.ParseMaxFeedrateX(root).Should().Be(500);
        _ = OrcaMachineProfileFields.ParseHasHeatedBed(root).Should().BeTrue();
        _ = OrcaMachineProfileFields.ParseHasHeatedChamber(root).Should().BeFalse();
        _ = OrcaMachineProfileFields.ParseNozzleDiameter(root).Should().Be(0.4);
        _ = OrcaMachineProfileFields.ParseNozzleTypeRaw(root).Should().Be("brass");
        _ = OrcaMachineProfileFields.ParseMotionTypeRaw(root).Should().Be("corexy");
        _ = OrcaMachineProfileFields.ParseMaxHotendTemperature(root).Should().Be(300);
    }
}
