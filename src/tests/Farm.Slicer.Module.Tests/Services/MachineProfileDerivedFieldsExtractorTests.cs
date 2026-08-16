using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Services;
using FluentAssertions;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Unit tests for <see cref="MachineProfileDerivedFieldsExtractor"/>'s documented fail-safe
/// contract (#1615 PR-2, carried over from #1614 AC-2): malformed, absent, or
/// unexpectedly-shaped machine-profile JSON must degrade to "still missing" (null/empty derived
/// facts) rather than throwing, since this feeds the calibration eligibility decision path via
/// <see cref="CalibrationProfileResolver"/>.
/// </summary>
public sealed class MachineProfileDerivedFieldsExtractorTests
{
    [Fact]
    public void Extract_WithNullOrWhitespaceInput_ReturnsEmptyFacts()
    {
        MachineProfileDerivedFields nullResult = MachineProfileDerivedFieldsExtractor.Extract(null);
        MachineProfileDerivedFields emptyResult = MachineProfileDerivedFieldsExtractor.Extract(string.Empty);
        MachineProfileDerivedFields whitespaceResult = MachineProfileDerivedFieldsExtractor.Extract("   ");

        _ = nullResult.Should().Be(MachineProfileDerivedFields.Empty);
        _ = emptyResult.Should().Be(MachineProfileDerivedFields.Empty);
        _ = whitespaceResult.Should().Be(MachineProfileDerivedFields.Empty);
    }

    [Theory]
    [InlineData("{not valid json")]
    [InlineData("{\"unterminated\": ")]
    [InlineData("not json at all")]
    public void Extract_WithMalformedJson_ReturnsEmptyFactsWithoutThrowing(string rawJson)
    {
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(rawJson);

        _ = result.Should().Be(MachineProfileDerivedFields.Empty);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void Extract_WithNonObjectRoot_ReturnsEmptyFactsWithoutThrowing(string rawJson)
    {
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(rawJson);

        _ = result.Should().Be(MachineProfileDerivedFields.Empty);
    }

    [Fact]
    public void Extract_WithNoRecognizedFields_ReturnsAllNullFacts()
    {
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(
            """{"some_unrelated_field": "value", "another": 123}""");

        _ = result.Should().Be(MachineProfileDerivedFields.Empty);
    }

    [Fact]
    public void Extract_WithMalformedPrintableAreaPointStrings_IgnoresUnparsablePointsAndDerivesNoGeometry()
    {
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(
            """{"printable_area": ["not-a-point", "also,bad", "250"]}""");

        _ = result.PrintablePolygon.Should().BeNull();
        _ = result.BedOriginX.Should().BeNull();
        _ = result.BuildVolumeX.Should().BeNull();
    }

    [Fact]
    public void Extract_WithEmptyPrintableAreaArray_ReturnsNoGeometry()
    {
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(
            """{"printable_area": []}""");

        _ = result.PrintablePolygon.Should().BeNull();
        _ = result.BedOriginX.Should().BeNull();
        _ = result.BuildVolumeX.Should().BeNull();
    }

    [Fact]
    public void Extract_WithWellFormedPrintableAreaAsPointArray_DerivesPolygonAndBoundingBox()
    {
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(
            """{"printable_area": ["0x0", "250x0", "250x250", "0x250"]}""");

        _ = result.PrintablePolygon.Should().HaveCount(4);
        _ = result.BedOriginX.Should().Be(0);
        _ = result.BedOriginY.Should().Be(0);
        _ = result.BuildVolumeX.Should().Be(250);
        _ = result.BuildVolumeY.Should().Be(250);
    }

    [Fact]
    public void Extract_WithWellFormedPrintableAreaAsCommaJoinedString_DerivesPolygonAndBoundingBox()
    {
        // Matches the OrcaSlicer worker's real-world profile shape (#1613 §5): a single
        // comma-joined string rather than a JSON array of point strings.
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(
            """{"printable_area": "0x0,220x0,220x220,0x220"}""");

        _ = result.PrintablePolygon.Should().HaveCount(4);
        _ = result.BedOriginX.Should().Be(0);
        _ = result.BedOriginY.Should().Be(0);
        _ = result.BuildVolumeX.Should().Be(220);
        _ = result.BuildVolumeY.Should().Be(220);
    }

    [Fact]
    public void Extract_WithNonZeroOriginPrintableArea_DerivesBedOriginAsMinAndBuildVolumeAsSpan()
    {
        // A non-origin-anchored bed exercises the actual bounding-box math (bedOrigin = min,
        // buildVolume = max - min) rather than the degenerate all-zero-origin case where every
        // fixture elsewhere happens to make min == 0 and max - min == max indistinguishable.
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(
            """{"printable_area": ["50x30", "300x30", "300x300", "50x300"]}""");

        _ = result.PrintablePolygon.Should().HaveCount(4);
        _ = result.BedOriginX.Should().Be(50);
        _ = result.BedOriginY.Should().Be(30);
        _ = result.BuildVolumeX.Should().Be(250);
        _ = result.BuildVolumeY.Should().Be(270);
    }

    [Theory]
    [InlineData("not-a-real-nozzle-type")]
    [InlineData("")]
    public void Extract_WithUnrecognizedOrEmptyNozzleType_ReturnsNullNozzleType(string rawValue)
    {
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(
            $$"""{"nozzle_type": "{{rawValue}}"}""");

        _ = result.NozzleType.Should().BeNull();
    }

    [Theory]
    [InlineData("not-a-real-motion-type")]
    [InlineData("")]
    public void Extract_WithUnrecognizedOrEmptyPrinterType_ReturnsNullMotionType(string rawValue)
    {
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(
            $$"""{"printer_type": "{{rawValue}}"}""");

        _ = result.MotionType.Should().BeNull();
    }

    [Fact]
    public void Extract_WithNonNumericAccelerationValue_ReturnsNullAcceleration()
    {
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(
            """{"machine_max_acceleration_x": ["not-a-number"]}""");

        _ = result.MaxAcceleration.Should().BeNull();
    }

    [Fact]
    public void Extract_WithEmptyNumericArrayFields_ReturnsNullRatherThanThrowing()
    {
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(
            """
            {
                "machine_max_acceleration_x": [],
                "machine_max_speed_x": [],
                "nozzle_diameter": [],
                "max_hotend_temp": []
            }
            """);

        _ = result.MaxAcceleration.Should().BeNull();
        _ = result.MaxTravelSpeed.Should().BeNull();
        _ = result.NozzleDiameter.Should().BeNull();
        _ = result.HotendMaxTemperature.Should().BeNull();
    }

    [Fact]
    public void Extract_WithFullySpecifiedProfile_DerivesAllFacts()
    {
        MachineProfileDerivedFields result = MachineProfileDerivedFieldsExtractor.Extract(
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

        _ = result.PrintablePolygon.Should().HaveCount(4);
        _ = result.BuildVolumeZ.Should().Be(250);
        _ = result.MaxAcceleration.Should().Be(10000);
        _ = result.MaxTravelSpeed.Should().Be(500);
        _ = result.HasHeatedBed.Should().BeTrue();
        _ = result.HasHeatedChamber.Should().BeFalse();
        _ = result.NozzleDiameter.Should().Be(0.4);
        _ = result.NozzleType.Should().Be(NozzleType.Brass);
        _ = result.MotionType.Should().Be(CalibrationMotionType.CoreXY);
        _ = result.NozzleMaxTemperature.Should().Be(300);
        _ = result.HotendMaxTemperature.Should().Be(300);
    }
}
