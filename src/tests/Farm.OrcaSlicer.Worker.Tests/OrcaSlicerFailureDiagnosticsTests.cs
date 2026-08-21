using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Models;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Regression coverage for issue #1811, where three of five library models failed with
/// <c>OrcaSlicer failed with exit code 156: Errors</c> and the surfaced text was too thin to act on.
/// </summary>
/// <remarks>
/// The console fixtures here are the byte-exact output captured from the pinned OrcaSlicer 2.4.2
/// AppImage while reproducing that issue against the same Phrozen Arco profiles and the same models.
/// Two properties of the real run drive these tests:
/// <list type="bullet">
/// <item><description>
/// The engine writes <c>Errors</c> (its own <c>ex.what()</c>) to stderr and
/// <c>run found error, return -100, exit...</c> to stdout, but the worker launches it through
/// <c>xvfb-run</c>, whose final line is <c>"$@" 2&gt;&amp;1</c> — so in every containerized deployment
/// both lines arrive on stdout and stderr is empty.
/// </description></item>
/// <item><description>
/// The only actionable diagnostic is in <c>result.json</c>, which the engine always writes into
/// <c>--outputdir</c> before exiting.
/// </description></item>
/// </list>
/// </remarks>
public sealed class OrcaSlicerFailureDiagnosticsTests
{
    /// <summary>Exactly what the worker's combined stream carries under xvfb-run for a -100 failure.</summary>
    private const string CombinedConsoleOutput = "Errors\nrun found error, return -100, exit...\n";

    /// <summary>Byte-exact <c>result.json</c> written by OrcaSlicer 2.4.2 for the failing models.</summary>
    private const string SlicingErrorResultJson = """
        {
            "error_string": "Failed slicing the model. Please verify the slicing of all plates on Orca Slicer before uploading.",
            "export_time": 0,
            "layer_height": 0.20000000298023224,
            "plate_index": 1,
            "prepare_time": 0,
            "return_code": -100,
            "sparse_infill_density": 15.0,
            "wall_loops": 2
        }
        """;

    [Fact(DisplayName = "The detail carries OrcaSlicer's own error_string rather than the bare token \"Errors\"")]
    public void Describe_WithResultJson_LeadsWithEngineErrorString()
    {
        OrcaSlicerFailureDiagnostics.OrcaResult? result =
            OrcaSlicerFailureDiagnostics.ParseResult(SlicingErrorResultJson);

        OrcaSlicerFailureDiagnostics.Diagnosis diagnosis =
            OrcaSlicerFailureDiagnostics.Describe(156, CombinedConsoleOutput, result);

        _ = diagnosis.Detail.Should().Contain(
            "Failed slicing the model.",
            "result.json is the only authoritative statement of what the engine decided");
        _ = diagnosis.Detail.Should().Contain("CLI_SLICING_ERROR", "the symbolic name lets an admin find the exit site");
        _ = diagnosis.Detail.Should().Contain("-100", "the signed CLI return code is what OrcaSlicer's own table is keyed on");
        _ = diagnosis.Detail.Should().NotBe(
            "OrcaSlicer failed with exit code 156: Errors",
            "that exact string is the issue #1811 regression");
    }

    [Fact(DisplayName = "Every informative console line is kept, not just the first match")]
    public void Describe_KeepsAllInformativeLines()
    {
        OrcaSlicerFailureDiagnostics.Diagnosis diagnosis =
            OrcaSlicerFailureDiagnostics.Describe(156, CombinedConsoleOutput, result: null);

        // The old FirstOrDefault scan stopped at "Errors" and discarded the line that actually
        // identified the failure.
        _ = diagnosis.Detail.Should().Contain("Errors");
        _ = diagnosis.Detail.Should().Contain(
            "run found error, return -100",
            "the second line was the one being dropped");
    }

    [Fact(DisplayName = "A -100 failure classifies as SlicingEngineRejectedModel and hints at auto-orienting")]
    public void Describe_SlicingError_ClassifiesAndHints()
    {
        OrcaSlicerFailureDiagnostics.OrcaResult? result =
            OrcaSlicerFailureDiagnostics.ParseResult(SlicingErrorResultJson);

        OrcaSlicerFailureDiagnostics.Diagnosis diagnosis =
            OrcaSlicerFailureDiagnostics.Describe(156, CombinedConsoleOutput, result);

        _ = diagnosis.Reason.Should().Be(SliceFailureReason.SlicingEngineRejectedModel);

        string? hint = SliceFailureHints.For(diagnosis.Reason);
        _ = hint.Should().NotBeNull();
        _ = hint.Should().Contain(
            "Auto-orient plate",
            "the hint must name the control that actually fixes this, not offer generic advice");
        _ = hint.Should().Contain(
            "most often",
            "-100 is a generic engine catch-all, so orientation must be phrased as a likely cause");
    }

    [Theory(DisplayName = "Exit statuses map to the signed CLI codes OrcaSlicer documents")]
    [InlineData(156, -100, SliceFailureReason.SlicingEngineRejectedModel)]
    [InlineData(239, -17, SliceFailureReason.ProfileNotCompatible)]
    [InlineData(253, -3, SliceFailureReason.ModelFileUnreadable)]
    [InlineData(206, -50, SliceFailureReason.NoPrintableObjects)]
    [InlineData(204, -52, SliceFailureReason.ModelOutsideBuildVolume)]
    public void ResolveReturnCode_MapsPosixExitStatusToSignedCode(
        int exitCode,
        int expectedReturnCode,
        SliceFailureReason expectedReason)
    {
        // A POSIX exit status is only the low 8 bits, so OrcaSlicer's -100 is observed as 156. Each
        // pair here was observed from the real CLI while reproducing issue #1811, or transcribed
        // from CLI_* in src/libslic3r/Utils.hpp at tag v2.4.2.
        int? resolved = OrcaSlicerFailureDiagnostics.ResolveReturnCode(result: null, exitCode);

        _ = resolved.Should().Be(expectedReturnCode);
        _ = OrcaSlicerFailureDiagnostics.Classify(resolved).Should().Be(expectedReason);
    }

    [Fact(DisplayName = "result.json's return_code wins over the truncated POSIX exit status")]
    public void ResolveReturnCode_PrefersResultJson()
    {
        OrcaSlicerFailureDiagnostics.OrcaResult result = new(-58, "timed out", []);

        // 156 alone would decode to -100; the recorded code is exact and must take precedence,
        // which also disambiguates the 129-159 range it shares with signal terminations.
        _ = OrcaSlicerFailureDiagnostics.ResolveReturnCode(result, 156).Should().Be(-58);
        _ = OrcaSlicerFailureDiagnostics.Classify(-58).Should().Be(SliceFailureReason.SlicingTimedOut);
    }

    [Fact(DisplayName = "An exit status that matches no known CLI code stays unclassified")]
    public void ResolveReturnCode_UnknownStatus_IsNotGuessed()
    {
        // 137 is SIGKILL (128 + 9), not a CLI code; decoding it to -119 would invent a diagnosis.
        _ = OrcaSlicerFailureDiagnostics.ResolveReturnCode(result: null, 137).Should().BeNull();
        _ = OrcaSlicerFailureDiagnostics.Classify(null).Should().Be(SliceFailureReason.SlicerFailed);
    }

    [Fact(DisplayName = "Per-plate warning text from result.json reaches the detail")]
    public void ParseResult_CarriesPlateWarnings()
    {
        const string Json = """
            {
                "return_code": -100,
                "error_string": "Failed slicing the model.",
                "sliced_plates": [
                    { "id": 1, "warning_message": "Empty layers around bottom are detected." }
                ]
            }
            """;

        OrcaSlicerFailureDiagnostics.OrcaResult? result = OrcaSlicerFailureDiagnostics.ParseResult(Json);

        _ = result.Should().NotBeNull();
        _ = result!.PlateWarnings.Should().ContainSingle()
            .Which.Should().Be("Empty layers around bottom are detected.");

        OrcaSlicerFailureDiagnostics.Diagnosis diagnosis =
            OrcaSlicerFailureDiagnostics.Describe(156, CombinedConsoleOutput, result);
        _ = diagnosis.Detail.Should().Contain("Empty layers around bottom are detected.");
    }

    [Fact(DisplayName = "[error]-tagged lines are preferred and all of them are kept")]
    public void CollectInformativeLines_PrefersTaggedLines()
    {
        const string Output =
            "[2026-08-21 14:24:28.386] [0x7f00] [error] first problem\n" +
            "some unrelated progress line\n" +
            "[2026-08-21 14:24:28.400] [0x7f00] [error] second problem\n";

        IReadOnlyList<string> lines = OrcaSlicerFailureDiagnostics.CollectInformativeLines(Output);

        _ = lines.Should().Equal("first problem", "second problem");
    }

    [Fact(DisplayName = "A missing or unparseable result.json never masks the failure")]
    public void ParseResult_Invalid_ReturnsNullAndDescribeStillReports()
    {
        _ = OrcaSlicerFailureDiagnostics.ParseResult("{ not json").Should().BeNull();
        _ = OrcaSlicerFailureDiagnostics.ParseResult(null).Should().BeNull();
        _ = OrcaSlicerFailureDiagnostics.TryReadResult("   ").Should().BeNull();

        OrcaSlicerFailureDiagnostics.Diagnosis diagnosis =
            OrcaSlicerFailureDiagnostics.Describe(156, CombinedConsoleOutput, result: null);

        _ = diagnosis.Detail.Should().Contain("exit code 156");
        _ = diagnosis.Reason.Should().Be(SliceFailureReason.SlicingEngineRejectedModel);
    }

    [Fact(DisplayName = "A silent failure says so rather than reporting an empty detail")]
    public void Describe_NoOutputAtAll_StatesThatExplicitly()
    {
        OrcaSlicerFailureDiagnostics.Diagnosis diagnosis =
            OrcaSlicerFailureDiagnostics.Describe(1, output: null, result: null);

        _ = diagnosis.Detail.Should().Contain("no diagnostic output");
        _ = diagnosis.Reason.Should().Be(SliceFailureReason.SlicerFailed);
    }

    [Fact(DisplayName = "Every defined failure reason has a hint, and unknown values get none")]
    public void SliceFailureHints_CoverEveryDefinedReason()
    {
        foreach (SliceFailureReason reason in Enum.GetValues<SliceFailureReason>())
        {
            _ = SliceFailureHints.For(reason).Should().NotBeNullOrWhiteSpace(
                $"{reason} is reported to non-admins, who have no other way to learn why a job failed");
        }

        // Guards the client-safe channel against a value written by a differently-versioned worker.
        _ = SliceFailureHints.For((SliceFailureReason)9999).Should().BeNull();
    }

    [Fact(DisplayName = "The composed detail stays inside the API's fail-message length budget")]
    public void Describe_LongOutput_IsBounded()
    {
        string noisy = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"error number {i}"));

        OrcaSlicerFailureDiagnostics.Diagnosis diagnosis =
            OrcaSlicerFailureDiagnostics.Describe(156, noisy, result: null);

        _ = diagnosis.Detail.Length.Should().BeLessThanOrEqualTo(
            1000,
            "HttpJobPollerService truncates at 1000 and FailSliceJobRequest caps at 1024");
    }
}
