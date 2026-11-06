using System.Security.Cryptography;
using System.Text;
using Farm.Modules.Gcode.Services.Gcode.Safety;
using Farm.Web.Api.Services.Gcode.Safety;
using Xunit;

namespace Farm.Modules.Gcode.Tests.Services.Gcode.Safety;

public sealed class GcodeSafetyValidatorTests
{
    private static readonly GcodeSafetyValidator Validator = new();

    [Theory]
    [InlineData(GcodeSafetyCheckpoint.BeforeSendToPrinter)]
    [InlineData(GcodeSafetyCheckpoint.BeforeQueueing)]
    public void Validate_NonEmptyInput_PreservesReportContract(GcodeSafetyCheckpoint checkpoint)
    {
        const string Program = "; metadata\r\nPRINT_START\r\nG90\nM83\n" +
            "G1 X10 Y10 E1 F1500 ; trailing comment\nPRINT_END\n";
        DateTime before = DateTime.UtcNow;

        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(new(Program, checkpoint));

        Assert.True(result.IsValid);
        Assert.Empty(result.Problems);
        GcodeSafetyReport report = Assert.IsType<GcodeSafetyReport>(result.Value);
        Assert.Equal(5, report.CommandCount);
        Assert.Equal(checkpoint, report.Checkpoint);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Program)))
            .ToLowerInvariant(), report.GcodeSha256);
        Assert.InRange(report.ValidatedAtUtc, before, DateTime.UtcNow);
        Assert.Equal(DateTimeKind.Utc, report.ValidatedAtUtc.Kind);
    }

    [Theory]
    [InlineData("RUN_SHELL_COMMAND CMD=example")]
    [InlineData("  run_shell_command\tCMD=example ; comment")]
    [InlineData("N1 RUN_SHELL_COMMAND CMD=example")]
    [InlineData("N10RUN_SHELL_COMMAND CMD=example")]
    [InlineData("/bin/sh example")]
    [InlineData("cmd.exe example")]
    [InlineData("powershell example")]
    [InlineData("curl https://example.invalid")]
    [InlineData("wget https://example.invalid")]
    [InlineData("nc example")]
    [InlineData("ssh example")]
    [InlineData("M104 S999")]
    [InlineData("M109 S999")]
    [InlineData("M140 S999")]
    [InlineData("M190 S999")]
    [InlineData("M141 S999")]
    [InlineData("M191 S999")]
    [InlineData("M204 S99999")]
    [InlineData("SET_VELOCITY_LIMIT ACCEL=99999 VELOCITY=99999")]
    [InlineData("G1 X10 E1 F999999")]
    [InlineData("G0 X10 F999999")]
    [InlineData("G1 X999 Y-999 Z999")]
    [InlineData("G91\nG1 X999 Y999 Z999")]
    [InlineData("M83\nG1 X10 E999 F999999")]
    [InlineData("G1 X90 Y90")]
    [InlineData("G1 X10 Y10")]
    [InlineData("TUNING_TOWER COMMAND=SET_PRESSURE_ADVANCE PARAMETER=ADVANCE START=0 FACTOR=.005")]
    [InlineData("SET_PRESSURE_ADVANCE ADVANCE=2.5")]
    [InlineData("M83\nG1 E-15")]
    [InlineData("M82\nG92 E100\nG1 E80")]
    [InlineData("CUSTOM_FIRMWARE_MACRO")]
    [InlineData("M117 password https://example.invalid /home/operator/file C:\\profiles\\file")]
    [InlineData("not even g-code \0 \uFEFF")]
    [InlineData("G1 X99999999999999999999999999999999999999999999999999999999999999999")]
    public void Validate_CommandContent_IsNeverRejected(string program)
    {
        GcodeSafetyResult<GcodeSafetyReport> result = Validator.Validate(
            new(program, GcodeSafetyCheckpoint.BeforeSendToPrinter));

        Assert.True(result.IsValid);
        Assert.Empty(result.Problems);
        Assert.NotNull(result.Value);
    }

    [Theory]
    [InlineData(" ", 0)]
    [InlineData("\r\n\t", 0)]
    [InlineData(";PF_SEG_BEGIN\n;PF_SEG_END", 0)]
    [InlineData("; api_key=example /bin/sh RUN_SHELL_COMMAND", 0)]
    [InlineData("G1 X1 ; comment\n\n; full line\nPRINT_END", 2)]
    [InlineData("RUN_SHELL_COMMAND CMD=example", 1)]
    public void Validate_NonEmptyText_CountsLinesWithoutInterpretingThem(string program, int count)
    {
        var result = Validator.Validate(new(program, GcodeSafetyCheckpoint.BeforeQueueing));

        Assert.True(result.IsValid);
        Assert.Equal(count, result.Value!.CommandCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(program)))
            .ToLowerInvariant(), result.Value.GcodeSha256);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_NullOrEmptyProgram_PreservesExactRejection(string? program)
    {
        var result = Validator.Validate(new(program, GcodeSafetyCheckpoint.BeforeQueueing));

        Assert.False(result.IsValid);
        Assert.Null(result.Value);
        Assert.Equal(new GcodeSafetyProblem(
            GcodeSafetyProblemCodes.Malformed, "gcode", "The g-code program is empty."),
            Assert.Single(result.Problems));
    }

    [Fact]
    public void Validate_NullRequest_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Validator.Validate(null!));
    }
}
