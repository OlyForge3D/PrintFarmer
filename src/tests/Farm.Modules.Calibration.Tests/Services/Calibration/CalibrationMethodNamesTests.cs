using Farm.Modules.Calibration.Services.Calibration;
using FluentAssertions;
using Xunit;

namespace Farm.Modules.Calibration.Tests.Services.Calibration;

/// <summary>
/// Direct coverage for the domain calibration-method catalogue (<see cref="CalibrationMethodNames"/>,
/// <see cref="CalibrationMethodSteps"/>, <see cref="CalibrationMeasurementRanges"/>). Prior to this
/// file, this catalogue was only exercised indirectly through
/// <see cref="CalibrationProjectServiceTests"/>'s <c>AppendObservationAsync</c>-style tests.
/// </summary>
public sealed class CalibrationMethodNamesTests
{
    [Fact]
    public void InputShaping_ToName_RoundTripsThroughTryParse()
    {
        // Issue #2139 acceptance: wire name must be exactly "input_shaping" and must round-trip.
        string name = CalibrationMethodNames.ToName(CalibrationMethod.InputShaping);

        name.Should().Be("input_shaping");
        CalibrationMethodNames.TryParse(name, out CalibrationMethod parsed).Should().BeTrue();
        parsed.Should().Be(CalibrationMethod.InputShaping);
    }

    [Fact]
    public void InputShaping_ToKind_ReturnsInputShaping()
    {
        // ToKind feeds CalibrationMeasurementRanges.ForKind and CalibrationObservation.Kind - must
        // match the wire name exactly so AppendObservationAsync looks up the (absent) range under
        // the same key ToName/TryParse use.
        CalibrationMethodNames.ToKind(CalibrationMethod.InputShaping).Should().Be("input_shaping");
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
    public void InputShaping_IsIncludedInAll()
    {
        CalibrationMethodNames.All.Should().Contain(CalibrationMethodNames.InputShaping);
    }

    [Fact]
    public void InputShaping_ForKind_HasNoDefinedMeasurementRange()
    {
        // Report-only (issue #2139): the worker-parsed resonance-frequency/damping-factor value is
        // recorded unvalidated, unlike temperature/flow_ratio/pressure_advance/max_volumetric_speed
        // which all have a defined physical range.
        CalibrationMeasurementRanges.ForKind(CalibrationMethodNames.InputShaping).Should().BeNull();
    }
}
