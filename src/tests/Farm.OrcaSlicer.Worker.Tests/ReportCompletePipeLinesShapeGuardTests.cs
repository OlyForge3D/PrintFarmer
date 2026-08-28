using System.Reflection;
using System.Text;
using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Regression coverage for issue #2148: <c>ReportCompletePipeLinesAsync</c> parses each line
/// from the OrcaSlicer progress pipe as JSON and previously called
/// <c>root.TryGetProperty("total_percent", ...)</c> / <c>msg.GetString()</c> /
/// <c>warn.GetString()</c> without first checking <c>ValueKind</c>. A non-object root, or an
/// object whose "message"/"warning" properties are not strings, threw an unguarded
/// <see cref="InvalidOperationException"/> that escaped the method's
/// <c>catch (JsonException)</c> block and crashed the job's progress-reporting task.
/// </summary>
public sealed class ReportCompletePipeLinesShapeGuardTests : IDisposable
{
    private readonly string _workingDirectory =
        Path.Join(Path.GetTempPath(), $"printfarmer-worker-pipeline-shape-{Guid.NewGuid():N}");
    private readonly HttpClient _httpClient = new();

    public void Dispose()
    {
        _httpClient.Dispose();
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    [Theory(DisplayName = "Non-object JSON lines on the progress pipe are skipped, not thrown")]
    [InlineData("42")]
    [InlineData("\"just a string\"")]
    [InlineData("[1, 2, 3]")]
    [InlineData("true")]
    [InlineData("null")]
    public async Task ReportCompletePipeLinesAsync_NonObjectRoot_DoesNotThrow(string jsonLine)
    {
        var reporter = new RecordingProgressReporter();
        OrcaSlicingPipelineService service = CreateService(reporter);
        var pending = new StringBuilder(jsonLine + "\n");

        Func<Task> act = () => InvokeReportCompletePipeLinesAsync(service, pending);

        await act.Should().NotThrowAsync();
        reporter.ProgressReports.Should().BeEmpty();
    }

    [Theory(DisplayName = "Non-string message/warning properties are ignored instead of throwing")]
    [InlineData("""{"total_percent": 50, "message": 123}""")]
    [InlineData("""{"total_percent": 50, "message": ["a","b"]}""")]
    [InlineData("""{"total_percent": 50, "warning": 7}""")]
    [InlineData("""{"total_percent": 50, "warning": {"nested": true}}""")]
    public async Task ReportCompletePipeLinesAsync_NonStringMessageOrWarning_DoesNotThrow(string jsonLine)
    {
        var reporter = new RecordingProgressReporter();
        OrcaSlicingPipelineService service = CreateService(reporter);
        var pending = new StringBuilder(jsonLine + "\n");

        Func<Task> act = () => InvokeReportCompletePipeLinesAsync(service, pending);

        await act.Should().NotThrowAsync();
        reporter.ProgressReports.Should().ContainSingle();
        reporter.ProgressReports[0].message.Should().Be("Slicing...");
    }

    [Fact(DisplayName = "A non-number total_percent falls back to no progress report rather than throwing")]
    public async Task ReportCompletePipeLinesAsync_NonNumberTotalPercent_DoesNotThrow()
    {
        var reporter = new RecordingProgressReporter();
        OrcaSlicingPipelineService service = CreateService(reporter);
        var pending = new StringBuilder("""{"total_percent": "fifty", "message": "hi"}""" + "\n");

        Func<Task> act = () => InvokeReportCompletePipeLinesAsync(service, pending);

        await act.Should().NotThrowAsync();
        reporter.ProgressReports.Should().BeEmpty();
    }

    [Fact(DisplayName = "A well-formed progress line still reports progress with the guard in place")]
    public async Task ReportCompletePipeLinesAsync_WellFormedLine_StillReportsProgress()
    {
        var reporter = new RecordingProgressReporter();
        OrcaSlicingPipelineService service = CreateService(reporter);
        var pending = new StringBuilder("""{"total_percent": 50, "message": "Slicing layer 3"}""" + "\n");

        await InvokeReportCompletePipeLinesAsync(service, pending);

        reporter.ProgressReports.Should().ContainSingle();
        reporter.ProgressReports[0].message.Should().Be("Slicing layer 3");
    }

    private static async Task InvokeReportCompletePipeLinesAsync(
        OrcaSlicingPipelineService service,
        StringBuilder pending)
    {
        MethodInfo method = typeof(OrcaSlicingPipelineService).GetMethod(
            "ReportCompletePipeLinesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReportCompletePipeLinesAsync is missing.");

        var task = (Task)method.Invoke(
            service,
            [Guid.NewGuid(), Guid.NewGuid(), pending, CancellationToken.None])!;
        await task;
    }

    private OrcaSlicingPipelineService CreateService(IProgressReporter reporter)
    {
        Dictionary<string, string?> values = new()
        {
            ["Worker:WorkingDirectory"] = _workingDirectory,
            ["SlicerApi:BaseUrl"] = "https://slicer.example.test:5246",
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var state = new WorkerStateService();
        state.SetRegisteredService(Guid.NewGuid(), "worker-secret");

        return new OrcaSlicingPipelineService(
            _httpClient,
            reporter,
            NullLogger<OrcaSlicingPipelineService>.Instance,
            configuration,
            state);
    }

    private sealed class RecordingProgressReporter : IProgressReporter
    {
        public List<(int progress, string message)> ProgressReports { get; } = [];

        public Task ReportProgressAsync(
            Guid jobId,
            Guid claimToken,
            int progress,
            string message,
            CancellationToken cancellationToken = default)
        {
            ProgressReports.Add((progress, message));
            return Task.CompletedTask;
        }

        public Task ReportCompletionAsync(
            DistributedSlicingJob job,
            SlicingResult result,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReportFailureAsync(
            Guid jobId,
            Guid claimToken,
            string errorMessage,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
