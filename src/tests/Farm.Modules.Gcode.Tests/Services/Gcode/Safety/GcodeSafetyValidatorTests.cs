using Farm.Modules.Gcode.Services.Gcode.Safety;
using Farm.Web.Api.Services.Gcode.Safety;
using FluentAssertions;
using Xunit;

namespace Farm.Modules.Gcode.Tests.Services.Gcode.Safety;

/// <summary>
/// Extraction-correctness tests for <see cref="GcodeSafetyValidator"/>, the general,
/// calibration-independent g-code safety pass extracted from the calibration-only validator
/// added by PR #947 (issue #1982).
/// </summary>
/// <remarks>
/// Unlike <c>CalibrationGcodeProgramValidatorTests</c>, these tests validate real slicer-style
/// g-code with no calibration allowlist — the case the send-to-printer path exercises.
/// </remarks>
public sealed class GcodeSafetyValidatorTests
{
    private const string CleanProgram =
        "G28\nG90\nM82\nM104 S200\nM140 S60\nG1 X10 Y10 Z0.2 F1500 E1.2\nTURN_OFF_HEATERS\nM84\n";

    private static readonly GcodeSafetyValidator Validator = new();

    [Fact]
    public void Validate_WithCleanNonCalibrationProgram_AndNoAllowlist_ReturnsCleanReport()
    {
        GcodeSafetyRequest request = new(
            GcodeSafetyLimits.Empty,
            CleanProgram,
            GcodeSafetyCheckpoint.BeforeSendToPrinter,
            AllowedCommands: null);

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(request);

        _ = result.IsValid.Should().BeTrue();
        _ = result.Problems.Should().BeEmpty();
        _ = result.Value!.Checkpoint.Should().Be(GcodeSafetyCheckpoint.BeforeSendToPrinter);
        _ = result.Value.CommandCount.Should().BeGreaterThan(0);
        _ = result.Value.GcodeSha256.Should().HaveLength(64);
    }

    [Fact]
    public void Validate_WithEmptyGcode_Rejects()
    {
        GcodeSafetyRequest request = new(
            GcodeSafetyLimits.Empty,
            string.Empty,
            GcodeSafetyCheckpoint.BeforeSendToPrinter);

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(request);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Problems.Select(p => p.Code).Should().Contain(GcodeSafetyProblemCodes.Malformed);
    }

    [Fact]
    public void Validate_WithUnsetTemperatureCeiling_SkipsTemperatureCheck()
    {
        // General/send-to-printer semantics: an unset ceiling means "skip this check", not
        // fail-closed — printers with incomplete profiles must not have valid gcode rejected.
        // Uses a gcode program commanding a nozzle temperature (260C) that WOULD be rejected if a
        // ceiling were configured (proven by the companion case below), so this test genuinely
        // demonstrates the skip is conditional on the ceiling being unset, not just that the
        // validator always accepts this program regardless of the check.
        const string ProgramWithHighNozzleTemperature = "G28\nM104 S260\nTURN_OFF_HEATERS\nM84\n";

        GcodeSafetyRequest requestWithNoCeiling = new(
            GcodeSafetyLimits.Empty,
            ProgramWithHighNozzleTemperature,
            GcodeSafetyCheckpoint.BeforeSendToPrinter);
        GcodeSafetyResult<GcodeSafetyReport> resultWithNoCeiling = Validator.Validate(requestWithNoCeiling);

        _ = resultWithNoCeiling.IsValid.Should().BeTrue();
        _ = resultWithNoCeiling.Problems.Should().BeEmpty();

        // Companion case: the exact same program is rejected once a ceiling is configured below
        // the commanded temperature, proving the skip above is genuinely conditional.
        GcodeSafetyLimits limitsWithCeiling = GcodeSafetyLimits.Empty with
        {
            Toolhead = new GcodeSafetyToolheadLimits(
                NozzleMaxTemperatureCelsius: 250,
                HotendMaxTemperatureCelsius: null,
                IsDirectDrive: null),
        };
        GcodeSafetyRequest requestWithCeiling = new(
            limitsWithCeiling,
            ProgramWithHighNozzleTemperature,
            GcodeSafetyCheckpoint.BeforeSendToPrinter);
        GcodeSafetyResult<GcodeSafetyReport> resultWithCeiling = Validator.Validate(requestWithCeiling);

        _ = resultWithCeiling.IsValid.Should().BeFalse();
        _ = resultWithCeiling.Problems.Select(p => p.Code).Should().Contain(GcodeSafetyProblemCodes.TemperatureAboveLimit);
    }

    [Fact]
    public void Validate_WithNozzleTemperatureAboveCeiling_Rejects()
    {
        GcodeSafetyLimits limits = GcodeSafetyLimits.Empty with
        {
            Toolhead = new GcodeSafetyToolheadLimits(
                NozzleMaxTemperatureCelsius: 250,
                HotendMaxTemperatureCelsius: null,
                IsDirectDrive: null),
        };
        GcodeSafetyRequest request = new(
            limits,
            "G28\nM104 S260\nTURN_OFF_HEATERS\nM84\n",
            GcodeSafetyCheckpoint.BeforeSendToPrinter);

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(request);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Problems.Select(p => p.Code).Should().Contain(GcodeSafetyProblemCodes.TemperatureAboveLimit);
    }

    [Fact]
    public void Validate_WithBedTemperatureAboveCeiling_Rejects()
    {
        GcodeSafetyLimits limits = GcodeSafetyLimits.Empty with
        {
            Machine = GcodeSafetyMachineLimits.Empty with { MaxBedTemperatureCelsius = 100 },
        };
        GcodeSafetyRequest request = new(
            limits,
            "G28\nM140 S110\nTURN_OFF_HEATERS\nM84\n",
            GcodeSafetyCheckpoint.BeforeSendToPrinter);

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(request);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Problems.Select(p => p.Code).Should().Contain(GcodeSafetyProblemCodes.TemperatureAboveLimit);
    }

    [Fact]
    public void Validate_WithoutHoming_RejectsUnsafeInitialization()
    {
        GcodeSafetyRequest request = new(
            GcodeSafetyLimits.Empty,
            "M104 S200\nG1 X10 Y10 E1.2 F1500\nTURN_OFF_HEATERS\nM84\n",
            GcodeSafetyCheckpoint.BeforeSendToPrinter);

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(request);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Problems.Select(p => p.Code).Should().Contain(GcodeSafetyProblemCodes.UnsafeInitialization);
    }

    [Fact]
    public void Validate_WithoutFinalReset_RejectsMissingFinalReset()
    {
        GcodeSafetyRequest request = new(
            GcodeSafetyLimits.Empty,
            "G28\nG1 X10 Y10 Z0.2 F1500\n",
            GcodeSafetyCheckpoint.BeforeSendToPrinter);

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(request);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Problems.Select(p => p.Code).Should().Contain(GcodeSafetyProblemCodes.MissingFinalReset);
    }

    [Fact]
    public void Validate_WithTuningTower_RejectsExplicitly()
    {
        GcodeSafetyRequest request = new(
            GcodeSafetyLimits.Empty,
            "G28\nTUNING_TOWER COMMAND=SET_PRESSURE_ADVANCE PARAMETER=ADVANCE START=0 FACTOR=.005\nTURN_OFF_HEATERS\nM84\n",
            GcodeSafetyCheckpoint.BeforeSendToPrinter);

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(request);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Problems.Select(p => p.Code).Should().Contain(GcodeSafetyProblemCodes.TuningTowerForbidden);
    }

    [Fact]
    public void Validate_WithEmbeddedCredential_RejectsRedaction()
    {
        GcodeSafetyRequest request = new(
            GcodeSafetyLimits.Empty,
            "G28\n; api_key=super-secret-value\nTURN_OFF_HEATERS\nM84\n",
            GcodeSafetyCheckpoint.BeforeSendToPrinter);

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(request);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Problems.Select(p => p.Code).Should().Contain(GcodeSafetyProblemCodes.ContainsCredential);
    }

    [Fact]
    public void Validate_WithHostCommand_RejectsRedaction()
    {
        GcodeSafetyRequest request = new(
            GcodeSafetyLimits.Empty,
            "G28\n; RUN_SHELL_COMMAND cmd=rm -rf /\nTURN_OFF_HEATERS\nM84\n",
            GcodeSafetyCheckpoint.BeforeSendToPrinter);

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(request);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Problems.Select(p => p.Code).Should().Contain(GcodeSafetyProblemCodes.ContainsHostCommand);
    }

    [Fact]
    public void Validate_WithAllowlistAndDisallowedCommand_Rejects()
    {
        GcodeSafetyRequest request = new(
            GcodeSafetyLimits.Empty,
            "G28\nM106 S255\nTURN_OFF_HEATERS\nM84\n",
            GcodeSafetyCheckpoint.BeforeSendToPrinter,
            AllowedCommands: ["G28", "TURN_OFF_HEATERS", "M84"]);

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(request);

        _ = result.IsValid.Should().BeFalse();
        _ = result.Problems.Select(p => p.Code).Should().Contain(GcodeSafetyProblemCodes.CommandNotAllowlisted);
    }

    [Fact]
    public void Validate_WithNoAllowlist_AcceptsArbitrarySlicerCommands()
    {
        // The real-world reason AllowedCommands is optional: ordinary slicer gcode uses a far
        // wider vocabulary (M106 fan control, etc.) than any calibration generator allowlist.
        GcodeSafetyRequest request = new(
            GcodeSafetyLimits.Empty,
            "G28\nM106 S255\nM107\nTURN_OFF_HEATERS\nM84\n",
            GcodeSafetyCheckpoint.BeforeSendToPrinter,
            AllowedCommands: null);

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(request);

        _ = result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_ThrowsOnNullRequest()
    {
        Action act = () => Validator.Validate(null!);

        _ = act.Should().Throw<ArgumentNullException>();
    }
}
