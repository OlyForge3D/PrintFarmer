using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class OrcaBinaryPathTests
{
    [Fact]
    public void DefaultBinaryPath_DetectorAndPipeline_UseSameExecutable()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SlicerApi:BaseUrl"] = "http://slicer-host:5246",
            })
            .Build();
        OrcaBinaryDetector detector = new(configuration);
        OrcaSlicingPipelineService pipeline = new(
            new HttpClient(),
            new NullProgressReporter(),
            NullLogger<OrcaSlicingPipelineService>.Instance,
            configuration,
            new WorkerStateService());

        detector.BinaryPath.Should().Be(OrcaBinaryDetector.DefaultBinaryPath);
        pipeline.OrcaSlicerBinaryPath.Should().Be(OrcaBinaryDetector.DefaultBinaryPath);
    }

    private sealed class NullProgressReporter : IProgressReporter
    {
        public Task ReportProgressAsync(
            Guid jobId,
            Guid claimToken,
            int progress,
            string message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

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
