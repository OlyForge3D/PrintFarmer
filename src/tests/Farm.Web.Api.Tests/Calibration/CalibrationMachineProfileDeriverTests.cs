using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Calibration;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Unit tests for <see cref="CalibrationMachineProfileDeriver"/>'s documented fail-safe
/// contract (#1614 AC-2): malformed, absent, or unexpectedly-shaped machine-profile JSON must
/// degrade to "still missing" (null/empty derived facts) rather than throwing, since this
/// parser sits directly in the calibration eligibility decision path.
/// </summary>
public sealed class CalibrationMachineProfileDeriverTests
{
    [Fact]
    public void Derive_WithNullOrWhitespaceInput_ReturnsEmptyFacts()
    {
        DerivedMachineFacts nullResult = CalibrationMachineProfileDeriver.Derive(null);
        DerivedMachineFacts emptyResult = CalibrationMachineProfileDeriver.Derive(string.Empty);
        DerivedMachineFacts whitespaceResult = CalibrationMachineProfileDeriver.Derive("   ");

        _ = nullResult.Should().Be(DerivedMachineFacts.Empty);
        _ = emptyResult.Should().Be(DerivedMachineFacts.Empty);
        _ = whitespaceResult.Should().Be(DerivedMachineFacts.Empty);
    }

    [Theory]
    [InlineData("{not valid json")]
    [InlineData("{\"unterminated\": ")]
    [InlineData("not json at all")]
    public void Derive_WithMalformedJson_ReturnsEmptyFactsWithoutThrowing(string rawJson)
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(rawJson);

        _ = result.Should().Be(DerivedMachineFacts.Empty);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void Derive_WithNonObjectRoot_ReturnsEmptyFactsWithoutThrowing(string rawJson)
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(rawJson);

        _ = result.Should().Be(DerivedMachineFacts.Empty);
    }

    [Fact]
    public void Derive_WithNoRecognizedFields_ReturnsAllNullFacts()
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(
            """{"some_unrelated_field": "value", "another": 123}""");

        _ = result.Should().Be(DerivedMachineFacts.Empty);
    }

    [Fact]
    public void Derive_WithMalformedPrintableAreaPointStrings_IgnoresUnparsablePointsAndDerivesNoGeometry()
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(
            """{"printable_area": ["not-a-point", "also,bad", "250"]}""");

        _ = result.PrintablePolygon.Should().BeNull();
        _ = result.BedOriginX.Should().BeNull();
        _ = result.BuildVolumeX.Should().BeNull();
    }

    [Fact]
    public void Derive_WithEmptyPrintableAreaArray_ReturnsNoGeometry()
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(
            """{"printable_area": []}""");

        _ = result.PrintablePolygon.Should().BeNull();
        _ = result.BedOriginX.Should().BeNull();
        _ = result.BuildVolumeX.Should().BeNull();
    }

    [Fact]
    public void Derive_WithWellFormedPrintableArea_DerivesPolygonAndBoundingBox()
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(
            """{"printable_area": ["0x0", "250x0", "250x250", "0x250"]}""");

        _ = result.PrintablePolygon.Should().HaveCount(4);
        _ = result.BedOriginX.Should().Be(0);
        _ = result.BedOriginY.Should().Be(0);
        _ = result.BuildVolumeX.Should().Be(250);
        _ = result.BuildVolumeY.Should().Be(250);
    }

    [Theory]
    [InlineData("not-a-real-nozzle-type")]
    [InlineData("")]
    public void Derive_WithUnrecognizedOrEmptyNozzleType_ReturnsNullNozzleType(string rawValue)
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(
            $$"""{"nozzle_type": "{{rawValue}}"}""");

        _ = result.NozzleType.Should().BeNull();
    }

    [Theory]
    [InlineData("not-a-real-motion-type")]
    [InlineData("")]
    public void Derive_WithUnrecognizedOrEmptyPrinterType_ReturnsNullMotionType(string rawValue)
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(
            $$"""{"printer_type": "{{rawValue}}"}""");

        _ = result.MotionType.Should().BeNull();
    }

    [Fact]
    public void Derive_WithNonNumericAccelerationValue_ReturnsNullAcceleration()
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(
            """{"machine_max_acceleration_x": ["not-a-number"]}""");

        _ = result.MaxAcceleration.Should().BeNull();
    }

    [Fact]
    public void Derive_WithEmptyNumericArrayFields_ReturnsNullRatherThanThrowing()
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(
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
    public void Derive_WithFullySpecifiedProfile_DerivesAllFacts()
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(
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
