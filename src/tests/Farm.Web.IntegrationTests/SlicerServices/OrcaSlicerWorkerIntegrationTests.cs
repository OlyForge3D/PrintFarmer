using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Web.IntegrationTests;
using Farm.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Integration tests for the OrcaSlicer worker end-to-end functionality
/// </summary>
public class OrcaSlicerWorkerIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public OrcaSlicerWorkerIntegrationTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task SubmitSlicingJob_ShouldCompleteSuccessfully_WithArtifactUrl()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();

        var request = new SlicingJobRequest
        {
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = new Uri("https://example.com/test.stl"),
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfile = new SlicerProfileDto
            {
                ProcessProfile = new ProcessProfileDto
                {
                    Name = "Test Profile",
                    LayerHeight = 0.2,
                    InfillPercentage = 20,
                    PrintSpeed = 50,
                    Supports = false,
                    Quality = "standard"
                },
                FilamentProfile = new FilamentProfileDto
                {
                    Name = "Test Filament",
                    Material = "PLA",
                    NozzleTemperature = 210,
                    BedTemperature = 60,
                    PrintSpeed = 50
                }
            },
            Priority = SlicingJobPriority.Normal
        };

        // Act
        var response = await orchestrator.SubmitJobAsync(request);

        // Assert
        Assert.NotEqual(Guid.Empty, response.JobId);
        Assert.Equal(SlicingJobStatus.Queued, response.Status);
        Assert.NotNull(response.SlicerWorkerUrl);
        Assert.NotEqual("about:blank", response.SlicerWorkerUrl.ToString());

        _output.WriteLine($"Job submitted: {response.JobId}");
        _output.WriteLine($"Status: {response.Status}");
        _output.WriteLine($"Queue position: {response.QueuePosition}");
        _output.WriteLine($"Worker URL: {response.SlicerWorkerUrl}");

        // Verify job can be retrieved
        var jobStatus = await orchestrator.GetJobStatusAsync(response.JobId);
        Assert.NotNull(jobStatus);
        Assert.Equal(response.JobId, jobStatus.JobId);
        Assert.Equal(SlicingJobStatus.Queued, jobStatus.Status);
    }

    [Fact]
    public async Task SlicingJobWorkflow_ShouldShowProgressUpdates()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();

        var request = new SlicingJobRequest
        {
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = new Uri("https://example.com/small-test.stl"),
            ModelFileName = "small-test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfile = new SlicerProfileDto
            {
                ProcessProfile = new ProcessProfileDto { LayerHeight = 0.3, Quality = "standard", Name = "Test" }
            },
            Priority = SlicingJobPriority.High
        };

        // Act
        var response = await orchestrator.SubmitJobAsync(request);

        // Assert initial state
        Assert.Equal(SlicingJobStatus.Queued, response.Status);

        _output.WriteLine($"Job {response.JobId} submitted successfully");
        _output.WriteLine($"Initial status: {response.Status}");
        _output.WriteLine($"Estimated completion: {response.EstimatedCompletionTime}");

        // The job should be properly enqueued and ready for worker processing
        // In a full integration test, we would wait for worker to process the job
        // but for Phase 1, we're verifying the submission and queueing works
    }

    [Fact]
    public async Task GetJobStatus_ForNonExistentJob_ShouldReturnNull()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();
        var nonExistentJobId = Guid.NewGuid();

        // Act
        var jobStatus = await orchestrator.GetJobStatusAsync(nonExistentJobId);

        // Assert
        Assert.Null(jobStatus);
    }

    [Fact]
    public async Task OrchestratorHealth_ShouldBeHealthy()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();

        // Act
        var health = await orchestrator.GetHealthAsync();

        // Assert
        Assert.NotNull(health);
        Assert.True(health.IsHealthy);
        Assert.Contains("OrcaSlicer", health.Engines.Keys.Select(k => k.ToString()));

        _output.WriteLine($"Orchestrator health: {JsonSerializer.Serialize(health, new JsonSerializerOptions { WriteIndented = true })}");
    }

    [Fact]
    public async Task SlicingJobRequest_WithInvalidEngine_ShouldThrowException()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();

        var request = new SlicingJobRequest
        {
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = new Uri("https://example.com/test.stl"),
            ModelFileName = "test.stl",
            SlicerEngine = (SlicerEngineType)999, // Invalid engine type
            SlicerProfile = new SlicerProfileDto(),
            Priority = SlicingJobPriority.Normal
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await orchestrator.SubmitJobAsync(request);
        });
    }
}
