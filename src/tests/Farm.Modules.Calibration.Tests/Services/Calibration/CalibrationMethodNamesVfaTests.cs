using Farm.Modules.Calibration.Services.Calibration;
using FluentAssertions;
using Xunit;

namespace Farm.Modules.Calibration.Tests.Services.Calibration;

/// <summary>
/// Covers the domain-catalogue wiring for the <c>Vfa</c> (resonance speed / VFA) calibration
/// method (issue #2140): wire-name round-trip, calibration kind, step sequence, and the
/// deliberate absence of a plausible-value measurement range. Per Dallas's architecture decision
/// on the issue, VFA — like Cornering and input-shaping — is report-only: the operator records an
/// observed resonance speed from the printed tower, and no value is ever verified or applied back
/// to the machine, so there is no <c>CalibrationObservation</c> schema change here.
/// </summary>
public sealed class CalibrationMethodNamesVfaTests
{
    [Fact]
    public void Vfa_WireName_Is_vfa()
    {
        CalibrationMethodNames.Vfa.Should().Be("vfa");
    }

    [Fact]
    public void TryParse_Vfa_RoundTripsToVfaMethod()
    {
        bool parsed = CalibrationMethodNames.TryParse("vfa", out CalibrationMethod method);

        parsed.Should().BeTrue();
        method.Should().Be(CalibrationMethod.Vfa);
        CalibrationMethodNames.ToName(CalibrationMethod.Vfa).Should().Be("vfa");
    }

    [Fact]
    public void All_ContainsVfa()
    {
        CalibrationMethodNames.All.Should().Contain("vfa");
    }

    [Fact]
    public void ToKind_Vfa_ReturnsResonanceSpeedKind()
    {
        CalibrationMethodNames.ToKind(CalibrationMethod.Vfa).Should().Be("resonance_speed");
    }

    [Fact]
    public void GetSequence_Vfa_MatchesDefaultFourStepSequence()
    {
        // VFA advances through the same setup/print/measure/select wizard as every other
        // catalogued method (D8, guide category 8 of 8); it does not need a divergent sequence.
        CalibrationMethodSteps.GetSequence(CalibrationMethod.Vfa).Should().Equal(
            CalibrationMethodSteps.Setup,
            CalibrationMethodSteps.Print,
            CalibrationMethodSteps.Measure,
            CalibrationMethodSteps.Select);
    }

    [Fact]
    public void CalibrationMeasurementRanges_ForKind_Vfa_ReturnsNull()
    {
        // Deliberate, per Dallas's analysis: VFA is report-only, so there is no server-verified
        // "correct" resonance speed value to bound-check against — the operator's observation is
        // recorded as-is. The API must treat a null range here as "no server-side plausibility
        // check", not as a bug.
        CalibrationMeasurementRanges.ForKind(CalibrationMethodNames.ToKind(CalibrationMethod.Vfa))
            .Should().BeNull();
    }
}
