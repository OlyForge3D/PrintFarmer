using Farm.Slicer.Module.Models;
using FluentAssertions;
using Xunit;

namespace Farm.Modules.Calibration.Tests.Services.Calibration;

/// <summary>
/// Issue #2161 AC #7 (persisted-data audit) coverage: every wire name the pre-unification saga
/// vocabulary ever wrote into <c>CalibrationAttempt.Method</c> must still parse today, even though
/// <c>Farm.Modules.Calibration</c>'s duplicate <c>CalibrationMethod</c>/<c>CalibrationMethodNames</c>
/// type has been deleted. Legacy names are parse-only: they must never be re-emitted by
/// <see cref="CalibrationMethods.ToWireName"/> or advertised via <see cref="CalibrationMethods.SupportedWireNames"/>/
/// <see cref="CalibrationMethods.ClientAcceptedWireNames"/>, so new writers are steered onto the
/// canonical name while old, already-persisted rows keep working.
/// </summary>
public sealed class CalibrationMethodLegacyAliasTests
{
    [Theory]
    [InlineData("temperature", CalibrationMethod.TemperatureTower)]
    [InlineData("flow_ratio_coarse", CalibrationMethod.FlowRatePass1)]
    [InlineData("flow_ratio_fine", CalibrationMethod.FlowRatePass2)]
    [InlineData("flow_ratio_high_range", CalibrationMethod.FlowRateYoloRecommended)]
    public void TryParse_LegacySagaWireName_ResolvesToCanonicalMethod(string legacyWireName, CalibrationMethod expected)
    {
        CalibrationMethods.TryParse(legacyWireName, out CalibrationMethod parsed).Should().BeTrue(
            $"a previously-persisted CalibrationAttempt.Method value of '{legacyWireName}' must still parse");
        parsed.Should().Be(expected);
    }

    [Theory]
    [InlineData("temperature")]
    [InlineData("flow_ratio_coarse")]
    [InlineData("flow_ratio_fine")]
    [InlineData("flow_ratio_high_range")]
    public void LegacySagaWireName_IsNeverAdvertisedAsSupportedOrClientAccepted(string legacyWireName)
    {
        // Legacy aliases are parse-only compatibility shims, not first-class wire names: they must
        // never appear in the catalogues used to advertise "supported methods" to clients, so new
        // callers are always steered onto the canonical spelling.
        CalibrationMethods.SupportedWireNames.Should().NotContain(legacyWireName);
        CalibrationMethods.ClientAcceptedWireNames.Should().NotContain(legacyWireName);
    }

    [Theory]
    [InlineData(CalibrationMethod.TemperatureTower)]
    [InlineData(CalibrationMethod.FlowRatePass1)]
    [InlineData(CalibrationMethod.FlowRatePass2)]
    [InlineData(CalibrationMethod.FlowRateYoloRecommended)]
    public void ToWireName_ForMethodsWithLegacyAliases_NeverEmitsTheLegacyName(CalibrationMethod method)
    {
        // The canonical wire name must always win when re-serializing - a legacy alias is an
        // input-only compatibility shim and must never leak back out as an output.
        string wireName = CalibrationMethods.ToWireName(method);

        wireName.Should().NotBe("temperature");
        wireName.Should().NotBe("flow_ratio_coarse");
        wireName.Should().NotBe("flow_ratio_fine");
        wireName.Should().NotBe("flow_ratio_high_range");
        CalibrationMethods.TryParse(wireName, out CalibrationMethod roundTripped).Should().BeTrue();
        roundTripped.Should().Be(method);
    }
}
