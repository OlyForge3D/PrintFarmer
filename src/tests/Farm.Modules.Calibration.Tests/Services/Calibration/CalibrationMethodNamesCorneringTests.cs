using Farm.Modules.Calibration.Services.Calibration;
using FluentAssertions;
using Xunit;

namespace Farm.Modules.Calibration.Tests.Services.Calibration;

/// <summary>
/// Covers the domain-catalogue wiring for the <c>Cornering</c> calibration method (issue #2138):
/// wire-name round-trip, calibration kind, step sequence, and the deliberate absence of a
/// plausible-value measurement range (cornering spans three different units and firmware
/// flavors — jerk in mm/s, junction deviation in mm, square corner velocity in mm/s — so no
/// single numeric range is meaningful the way it is for e.g. temperature or flow ratio).
/// </summary>
public sealed class CalibrationMethodNamesCorneringTests
{
    [Fact]
    public void Cornering_WireName_Is_cornering()
    {
        CalibrationMethodNames.Cornering.Should().Be("cornering");
    }

    [Fact]
    public void TryParse_Cornering_RoundTripsToCorneringMethod()
    {
        bool parsed = CalibrationMethodNames.TryParse("cornering", out CalibrationMethod method);

        parsed.Should().BeTrue();
        method.Should().Be(CalibrationMethod.Cornering);
        CalibrationMethodNames.ToName(CalibrationMethod.Cornering).Should().Be("cornering");
    }

    [Fact]
    public void All_ContainsCornering()
    {
        CalibrationMethodNames.All.Should().Contain("cornering");
    }

    [Fact]
    public void ToKind_Cornering_ReturnsCorneringKind()
    {
        CalibrationMethodNames.ToKind(CalibrationMethod.Cornering).Should().Be("cornering");
    }

    [Fact]
    public void GetSequence_Cornering_MatchesDefaultFourStepSequence()
    {
        // Cornering advances through the same setup/print/measure/select wizard as every other
        // catalogued method (D8); it does not need a divergent sequence.
        CalibrationMethodSteps.GetSequence(CalibrationMethod.Cornering).Should().Equal(
            CalibrationMethodSteps.Setup,
            CalibrationMethodSteps.Print,
            CalibrationMethodSteps.Measure,
            CalibrationMethodSteps.Select);
    }

    [Fact]
    public void CalibrationMeasurementRanges_ForKind_Cornering_ReturnsNull()
    {
        // Deliberate: unlike temperature/flow/pressure-advance, "cornering" spans three
        // incompatible units depending on firmware flavor (mm/s jerk, mm junction deviation, or
        // mm/s square corner velocity), so no single plausible-value range applies. The API must
        // treat a null range here as "no server-side plausibility check", not as a bug.
        CalibrationMeasurementRanges.ForKind(CalibrationMethodNames.ToKind(CalibrationMethod.Cornering))
            .Should().BeNull();
    }
}
