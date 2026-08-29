using Farm.Modules.Calibration.Services.Calibration;
using Farm.Slicer.Module.Models;
using FluentAssertions;
using Xunit;

namespace Farm.Modules.Calibration.Tests.Services.Calibration;

/// <summary>
/// Direct coverage for the domain calibration catalogue as unified onto the slicer's own
/// <see cref="CalibrationMethods"/> (issue #2161), plus the saga-specific behavior that remains
/// in <see cref="Farm.Modules.Calibration.Services.Calibration.CalibrationMethodSteps"/> and
/// <see cref="CalibrationMeasurementRanges"/>. Prior to this file, this catalogue was only
/// exercised indirectly through <see cref="CalibrationProjectServiceTests"/>'s
/// <c>AppendObservationAsync</c>-style tests.
/// </summary>
public sealed class CalibrationMethodNamesTests
{
    [Fact]
    public void InputShaping_ToWireName_RoundTripsThroughTryParse()
    {
        // Issue #2139 acceptance: wire name must be exactly "input_shaping" and must round-trip.
        string name = CalibrationMethods.ToWireName(CalibrationMethod.InputShaping);

        name.Should().Be("input_shaping");
        CalibrationMethods.TryParse(name, out CalibrationMethod parsed).Should().BeTrue();
        parsed.Should().Be(CalibrationMethod.InputShaping);
    }

    [Fact]
    public void InputShaping_ToKind_ReturnsInputShaping()
    {
        // ToKind feeds CalibrationMeasurementRanges.ForKind and CalibrationObservation.Kind - must
        // match the wire name exactly so AppendObservationAsync looks up the (absent) range under
        // the same key ToWireName/TryParse use.
        CalibrationMethodKinds.ToKind(CalibrationMethod.InputShaping).Should().Be("input_shaping");
    }

    [Fact]
    public void InputShaping_GetSequence_ReusesDefaultSequence()
    {
        // Dallas's architecture decision: input shaping needs no method-specific step sequence -
        // it reuses the same setup/print/measure/select flow every other simple method uses.
        IReadOnlyList<string> sequence = CalibrationMethodSteps.GetSequence(CalibrationMethod.InputShaping);

        sequence.Should().Equal(
            CalibrationMethodSteps.Setup,
            CalibrationMethodSteps.Print,
            CalibrationMethodSteps.Measure,
            CalibrationMethodSteps.Select);
    }

    [Fact]
    public void InputShaping_IsIncludedInSupportedWireNames()
    {
        CalibrationMethods.SupportedWireNames.Should().Contain("input_shaping");
    }

    [Fact]
    public void InputShaping_ForKind_HasNoDefinedMeasurementRange()
    {
        // Report-only (issue #2139): the worker-parsed resonance-frequency/damping-factor value is
        // recorded unvalidated, unlike temperature/flow_ratio/pressure_advance/max_volumetric_speed
        // which all have a defined physical range.
        CalibrationMeasurementRanges.ForKind("input_shaping").Should().BeNull();
    }
}
