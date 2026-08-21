using System.Text.Json;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Covers the worker→API junction for issue #1811: the diagnostic the pipeline composes must reach
/// the fail endpoint intact, and the redacted classification must travel structurally beside it.
/// </summary>
/// <remarks>
/// <see cref="OrcaSlicerFailureDiagnosticsTests"/> proves the diagnostic is composed correctly and
/// <c>SliceJobFailureReasonRoundTripTests</c> (in <c>Farm.Slicer.Module.Tests</c>) proves the API
/// persists it, redacts it, and exposes the safe channel. Neither of those touches the code that
/// carries one to the other, which is what this file exercises: the whole path is only covered if
/// this junction is too. That test project cannot reference the worker assemblies, so the handover
/// has to be asserted from this side.
/// </remarks>
public sealed class SlicerFailureReportTransmissionTests
{
    /// <summary>Byte-exact combined console output from a real exit-156 run under xvfb-run.</summary>
    private const string CombinedConsoleOutput = "Errors\nrun found error, return -100, exit...\n";

    /// <summary>Byte-exact <c>result.json</c> from that same run.</summary>
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

    [Fact(DisplayName =
        "A rejected model's composed diagnostic and reason both reach the fail payload intact")]
    public void CreateFailureReport_FromRealEngineFailure_CarriesDetailAndReason()
    {
        // Exactly what the pipeline does: compose from the engine's own output, then throw a
        // classified exception rather than a bare InvalidOperationException.
        OrcaSlicerFailureDiagnostics.Diagnosis diagnosis = OrcaSlicerFailureDiagnostics.Describe(
            156,
            CombinedConsoleOutput,
            OrcaSlicerFailureDiagnostics.ParseResult(SlicingErrorResultJson));

        Exception thrown = new SlicerEngineFailureException(diagnosis.Reason, diagnosis.Detail);

        // ...and exactly what HttpJobPollerService's catch block does with it.
        FailSliceJobRequest payload = HttpJobPollerService.CreateFailureReport(
            thrown.Message,
            HttpJobPollerService.ClassifyFailure(thrown));

        _ = payload.FailureReason.Should().Be(
            SliceFailureReason.SlicingEngineRejectedModel,
            "the classification must travel structurally, not be re-derived from the message");
        _ = payload.ErrorMessage.Should().Contain("Failed slicing the model.");
        _ = payload.ErrorMessage.Should().Contain("CLI_SLICING_ERROR");
        _ = payload.ErrorMessage.Should().Contain("run found error, return -100");
        _ = payload.ErrorMessage.Should().NotBe(
            "OrcaSlicer failed with exit code 156: Errors",
            "that exact string is the issue #1811 regression");
    }

    [Fact(DisplayName = "An unclassified failure reports the diagnostic but asserts no reason")]
    public void ClassifyFailure_NonEngineException_StaysUnclassified()
    {
        // A timeout, an IO error or a bug must not be guessed at: inventing a reason here would put
        // confident, wrong guidance in front of the operator.
        _ = HttpJobPollerService.ClassifyFailure(new TimeoutException("upload timed out")).Should().BeNull();
        _ = HttpJobPollerService.ClassifyFailure(new InvalidOperationException("boom")).Should().BeNull();
        _ = HttpJobPollerService.ClassifyFailure(null).Should().BeNull();

        FailSliceJobRequest payload = HttpJobPollerService.CreateFailureReport("upload timed out", null);
        _ = payload.FailureReason.Should().BeNull();
        _ = payload.ErrorMessage.Should().Be("upload timed out");
    }

    [Fact(DisplayName = "The default SlicerEngineFailureException reason is the unclassified fallback")]
    public void SlicerEngineFailureException_DefaultsToSlicerFailed()
    {
        _ = new SlicerEngineFailureException("something broke").Reason
            .Should().Be(SliceFailureReason.SlicerFailed);
        _ = new SlicerEngineFailureException("something broke", new InvalidOperationException()).Reason
            .Should().Be(SliceFailureReason.SlicerFailed);
    }

    [Fact(DisplayName = "An over-long diagnostic is truncated to the endpoint's contract budget")]
    public void CreateFailureReport_TruncatesToContractBudget()
    {
        FailSliceJobRequest payload = HttpJobPollerService.CreateFailureReport(
            new string('x', 5000),
            SliceFailureReason.SlicerFailed);

        // FailSliceJobRequest declares [MaxLength(1024)]; exceeding it would make MVC validation
        // reject the report and lose the failure entirely.
        _ = payload.ErrorMessage.Length.Should().Be(1000);
    }

    [Fact(DisplayName = "The fail payload serializes as camelCase with a string enum")]
    public void CreateFailureReport_SerializesToTheApiContract()
    {
        FailSliceJobRequest payload = HttpJobPollerService.CreateFailureReport(
            "OrcaSlicer failed with exit code 156 (CLI_SLICING_ERROR, -100)",
            SliceFailureReason.SlicingEngineRejectedModel);

        // JsonContent.Create uses web defaults, which is what the API is configured to read.
        string json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        _ = json.Should().Contain("\"errorMessage\":");
        _ = json.Should().Contain("\"failureReason\":\"SlicingEngineRejectedModel\"");
        _ = json.Should().NotContain("\"FailureReason\"", "the API contract is camelCase");
    }

    [Fact(DisplayName = "A worker with no classification omits the field, so an older API still binds")]
    public void CreateFailureReport_WithoutReason_RoundTripsThroughTheContract()
    {
        FailSliceJobRequest payload = HttpJobPollerService.CreateFailureReport("boom", null);
        string json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        FailSliceJobRequest? parsed = JsonSerializer.Deserialize<FailSliceJobRequest>(
            json, JsonSerializerOptions.Web);

        _ = parsed.Should().NotBeNull();
        _ = parsed!.ErrorMessage.Should().Be("boom");
        _ = parsed.FailureReason.Should().BeNull();

        // The reverse direction: a payload from a worker built before this field existed.
        FailSliceJobRequest? legacy = JsonSerializer.Deserialize<FailSliceJobRequest>(
            """{"errorMessage":"boom"}""", JsonSerializerOptions.Web);
        _ = legacy.Should().NotBeNull();
        _ = legacy!.ErrorMessage.Should().Be("boom");
        _ = legacy.FailureReason.Should().BeNull();
    }
}
