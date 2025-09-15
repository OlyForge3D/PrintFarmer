using Farm.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Integration tests for the OrcaSlicer worker end-to-end functionality
/// </summary>
// Uses orchestrator + DI with database-backed services; classify as DbHeavy
[Trait("Category", "Docker")]
[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerial")]
[TestTiming("DbHeavy")]
public class OrcaSlicerWorkerIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public OrcaSlicerWorkerIntegrationTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    private static SlicingJobRequest CreateRequest(string fileName = "test.stl", SlicingJobPriority priority = SlicingJobPriority.Normal)
    {
        return new SlicingJobRequest
        {
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = new Uri($"https://example.com/{fileName}"),
            ModelFileName = fileName,
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfile = new SlicerProfileDto
            {
                LayerHeight = 0.2,
                InfillPercentage = 20,
                PrintSpeed = 50,
                NozzleTemperature = 210,
                BedTemperature = 60,
                Material = "PLA"
            },
            Priority = priority
        };
    }

    [Fact]
    public async Task SubmitSlicingJob_ShouldCompleteSuccessfully_WithArtifactUrl()
    {
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();
        var response = await orchestrator.SubmitJobAsync(CreateRequest());

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
        Assert.Equal(response.JobId, jobStatus!.JobId);
        Assert.Equal(SlicingJobStatus.Queued, jobStatus.Status);
    }

    [Fact]
    public async Task SlicingJobWorkflow_ShouldShowProgressUpdates()
    {
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();
        var response = await orchestrator.SubmitJobAsync(CreateRequest("small-test.stl", SlicingJobPriority.High));
        Assert.Equal(SlicingJobStatus.Queued, response.Status);
    }

    [Fact]
    [TestTiming("JobStatus")]
    public async Task GetJobStatus_ForNonExistentJob_ShouldReturnNull()
    {
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();
        var jobStatus = await orchestrator.GetJobStatusAsync(Guid.NewGuid());
        Assert.Null(jobStatus);
    }

    [Fact]
    public async Task OrchestratorHealth_ShouldBeHealthy()
    {
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();
        var health = await orchestrator.GetHealthAsync();
        Assert.NotNull(health);
        Assert.True(health!.IsHealthy);
        Assert.Contains("OrcaSlicer", health.Engines.Keys.Select(k => k.ToString()));
    }

    [Fact]
    public async Task SlicingJobRequest_WithInvalidEngine_ShouldThrowException()
    {
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();
        var request = CreateRequest();
        request.SlicerEngine = (SlicerEngineType)999; // Invalid
        await Assert.ThrowsAsync<ArgumentException>(async () => await orchestrator.SubmitJobAsync(request));
    }
}
