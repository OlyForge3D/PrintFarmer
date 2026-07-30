using Farm.OrcaSlicer.Worker.Services;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Worker.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

public sealed class ModelDownloadRequestTests : IDisposable
{
    private readonly string _workingDirectory =
        Path.Combine(Path.GetTempPath(), $"printfarmer-worker-request-{Guid.NewGuid():N}");

    [Fact]
    public void CreateModelDownloadRequest_RelativeMultiModelRoute_UsesApiBaseAndClaimHeaders()
    {
        Guid serviceId = Guid.NewGuid();
        Guid claimToken = Guid.NewGuid();
        Guid leaseToken = Guid.NewGuid();
        var state = new WorkerStateService();
        state.SetRegisteredService(serviceId, "worker-secret");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SlicerApi:BaseUrl"] = "https://slicer.example.test:5246",
                ["Worker:WorkingDirectory"] = _workingDirectory,
            })
            .Build();
        var service = new OrcaSlicingPipelineService(
            new HttpClient(),
            new NullProgressReporter(),
            NullLogger<OrcaSlicingPipelineService>.Instance,
            configuration,
            state);

        using HttpRequestMessage request = service.CreateModelDownloadRequest(
            "/api/slice/123/models/1",
            claimToken,
            leaseToken,
            leaseFence: 7);

        request.RequestUri.Should().Be(new Uri("https://slicer.example.test:5246/api/slice/123/models/1"));
        request.Headers.GetValues("X-Worker-Key").Should().ContainSingle().Which.Should().Be("worker-secret");
        request.Headers.GetValues("X-Worker-Id").Should().ContainSingle().Which.Should().Be(serviceId.ToString());
        request.Headers.GetValues(WorkerClaimHeaders.ClaimToken).Should().ContainSingle().Which.Should().Be(claimToken.ToString());
        request.Headers.GetValues(WorkerLeaseHeaders.LeaseToken).Should().ContainSingle().Which.Should().Be(leaseToken.ToString());
        request.Headers.GetValues(WorkerLeaseHeaders.LeaseFence).Should().ContainSingle().Which.Should().Be("7");
    }

    [Theory]
    [InlineData("http://models.example.test/api/models/1")]
    [InlineData("https://models.example.test/api/models/1")]
    public void CreateModelDownloadRequest_AbsoluteHttpRoute_PreservesRoute(string modelUrl)
    {
        var state = new WorkerStateService();
        state.SetRegisteredService(Guid.NewGuid(), "worker-secret");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SlicerApi:BaseUrl"] = "https://slicer.example.test:5246",
                ["Worker:WorkingDirectory"] = _workingDirectory,
            })
            .Build();
        var service = new OrcaSlicingPipelineService(
            new HttpClient(),
            new NullProgressReporter(),
            NullLogger<OrcaSlicingPipelineService>.Instance,
            configuration,
            state);

        using HttpRequestMessage request = service.CreateModelDownloadRequest(
            modelUrl,
            Guid.NewGuid(),
            Guid.NewGuid(),
            leaseFence: 7);

        request.RequestUri.Should().Be(new Uri(modelUrl));
    }

    public void Dispose()
    {
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
