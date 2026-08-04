using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class SlicingMetadataTests : IDisposable
{
    private readonly string _workingDirectory =
        Path.Combine(Path.GetTempPath(), $"printfarmer-slicing-metadata-{Guid.NewGuid():N}");
    private readonly HttpClient _httpClient = new();

    [Theory]
    [InlineData(" 2.4.2 ", "2.4.1", "2.4.2")]
    [InlineData(null, "2.4.1", "2.4.1")]
    [InlineData(null, null, WorkerConstants.SlicerVersion)]
    public void PopulateResultMetadata_EngineVersionConfigurationFallbacks_UsesExpectedSlicerVersion(
        string? workerVersion,
        string? registryVersion,
        string expectedVersion)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worker:EngineVersion"] = workerVersion,
                ["SlicerRegistry:Version"] = registryVersion,
                ["Worker:WorkingDirectory"] = _workingDirectory,
            })
            .Build();
        var service = new OrcaSlicingPipelineService(
            _httpClient,
            new NullProgressReporter(),
            NullLogger<OrcaSlicingPipelineService>.Instance,
            configuration,
            new WorkerStateService());
        var result = new SlicingResult();
        var job = new DistributedSlicingJob { WorkerId = "worker-7" };

        service.PopulateResultMetadata(result, job, modelCount: 2);

        result.Metadata["SlicerVersion"].Should().Be($"OrcaSlicer {expectedVersion}");
        result.Metadata["WorkerId"].Should().Be("worker-7");
        result.Metadata["ModelCount"].Should().Be("2");
        DateTimeOffset.TryParse(result.Metadata["ProcessedAt"], out _).Should().BeTrue();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
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
