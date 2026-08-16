using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Services.Calibration;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Unit tests for <see cref="CalibrationMachineProfileDeriver"/>'s typed-field passthrough
/// contract (#1615 PR-2): the deriver no longer parses OrcaSlicer profile JSON itself &#8212;
/// that now happens once, producer-side, via the shared <c>Farm.Slicer.ProfileParsing</c>
/// library (see <c>Farm.Slicer.ProfileParsing.Tests</c> and
/// <c>Farm.Slicer.Module.Tests</c>'s <c>MachineProfileDerivedFieldsExtractorTests</c> for the
/// parsing/fail-safe coverage). This file only verifies the deriver reads the typed fields
/// straight off <see cref="ResolvedCalibrationProfile"/> without transformation.
/// </summary>
public sealed class CalibrationMachineProfileDeriverTests
{
    [Fact]
    public void Derive_WithNullProfile_ReturnsEmptyFacts()
    {
        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(null);

        _ = result.Should().Be(DerivedMachineFacts.Empty);
    }

    [Fact]
    public void Derive_WithProfileHavingNoDerivedFields_ReturnsEmptyFacts()
    {
        ResolvedCalibrationProfile machine = CreateMachine();

        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(machine);

        _ = result.Should().Be(DerivedMachineFacts.Empty);
    }

    [Fact]
    public void Derive_WithFullySpecifiedTypedFields_PassesThroughEveryFieldUnchanged()
    {
        CalibrationPointDto[] polygon =
        [
            new(0, 0),
            new(250, 0),
            new(250, 250),
            new(0, 250),
        ];
        ResolvedCalibrationProfile machine = CreateMachine() with
        {
            PrintablePolygon = polygon,
            BedOriginX = 0,
            BedOriginY = 0,
            BuildVolumeX = 250,
            BuildVolumeY = 250,
            BuildVolumeZ = 250,
            MotionType = CalibrationMotionType.CoreXY,
            MaxAcceleration = 10000,
            MaxTravelSpeed = 500,
            HasHeatedBed = true,
            HasHeatedChamber = false,
            NozzleDiameter = 0.4,
            NozzleType = NozzleType.Brass,
            NozzleMaxTemperature = 300,
            HotendMaxTemperature = 300,
        };

        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(machine);

        _ = result.PrintablePolygon.Should().BeEquivalentTo(polygon);
        _ = result.BedOriginX.Should().Be(0);
        _ = result.BedOriginY.Should().Be(0);
        _ = result.BuildVolumeX.Should().Be(250);
        _ = result.BuildVolumeY.Should().Be(250);
        _ = result.BuildVolumeZ.Should().Be(250);
        _ = result.MotionType.Should().Be(CalibrationMotionType.CoreXY);
        _ = result.MaxAcceleration.Should().Be(10000);
        _ = result.MaxTravelSpeed.Should().Be(500);
        _ = result.HasHeatedBed.Should().BeTrue();
        _ = result.HasHeatedChamber.Should().BeFalse();
        _ = result.NozzleDiameter.Should().Be(0.4);
        _ = result.NozzleType.Should().Be(NozzleType.Brass);
        _ = result.NozzleMaxTemperature.Should().Be(300);
        _ = result.HotendMaxTemperature.Should().Be(300);
    }

    [Fact]
    public void Derive_WithOnlySomeTypedFieldsPopulated_LeavesOthersNull()
    {
        ResolvedCalibrationProfile machine = CreateMachine() with
        {
            HasHeatedBed = true,
            NozzleDiameter = 0.4,
        };

        DerivedMachineFacts result = CalibrationMachineProfileDeriver.Derive(machine);

        _ = result.HasHeatedBed.Should().BeTrue();
        _ = result.NozzleDiameter.Should().Be(0.4);
        _ = result.PrintablePolygon.Should().BeNull();
        _ = result.BuildVolumeZ.Should().BeNull();
        _ = result.MotionType.Should().BeNull();
        _ = result.NozzleType.Should().BeNull();
    }

    private static ResolvedCalibrationProfile CreateMachine() =>
        new(
            Guid.NewGuid(),
            "machine",
            "Test Machine",
            "OrcaSlicer",
            "upstream",
            "2.4.0",
            "orca-json",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
}
