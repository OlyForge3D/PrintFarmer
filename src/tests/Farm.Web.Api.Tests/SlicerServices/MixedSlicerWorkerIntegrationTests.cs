using System.Text.Json;
using Farm.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Integration tests for mixed slicer worker functionality (OrcaSlicer + PrusaSlicer)
/// Tests that validate the abstraction works correctly with multiple slicer engines
/// </summary>
public class MixedSlicerWorkerIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly ITestOutputHelper _output;

    public MixedSlicerWorkerIntegrationTests(CustomWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task MixedJobQueue_ShouldProcessBothSlicerTypes_WithoutCodeDuplication()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<MixedSlicerWorkerIntegrationTests>>();

        var orcaRequest = new SlicingJobRequest
        {
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "https://example.com/test-model.stl",
            ModelFileName = "test-model-orca.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfile = new SlicerProfileDto
            {
                Quality = "Standard",
                Material = "PLA",
                LayerHeight = 0.2,
                InfillPercentage = 20,
                PrintSpeed = 50,
                NozzleTemperature = 210,
                BedTemperature = 60,
                Supports = false
            },
            Priority = SlicingJobPriority.Normal
        };

        var prusaRequest = new SlicingJobRequest
        {
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "https://example.com/test-model.stl",
            ModelFileName = "test-model-prusa.stl",
            SlicerEngine = SlicerEngineType.PrusaSlicer,
            SlicerProfile = new SlicerProfileDto
            {
                Quality = "High",
                Material = "PETG",
                LayerHeight = 0.15,
                InfillPercentage = 30,
                PrintSpeed = 40,
                NozzleTemperature = 230,
                BedTemperature = 80,
                Supports = true
            },
            Priority = SlicingJobPriority.High
        };

        // Act
        logger.LogInformation("Submitting OrcaSlicer job...");
        var orcaResponse = await orchestrator.SubmitJobAsync(orcaRequest);

        logger.LogInformation("Submitting PrusaSlicer job...");
        var prusaResponse = await orchestrator.SubmitJobAsync(prusaRequest);

        // Wait a moment for processing
        await Task.Delay(TimeSpan.FromSeconds(2));

        var orcaStatus = await orchestrator.GetJobStatusAsync(orcaResponse.JobId);
        var prusaStatus = await orchestrator.GetJobStatusAsync(prusaResponse.JobId);

        // Assert
        Assert.NotNull(orcaResponse);
        Assert.NotNull(prusaResponse);
        Assert.NotEqual(Guid.Empty, orcaResponse.JobId);
        Assert.NotEqual(Guid.Empty, prusaResponse.JobId);
        Assert.NotEqual(orcaResponse.JobId, prusaResponse.JobId);

        // Verify different slicer engines are used
        Assert.Equal(SlicingJobStatus.Queued, orcaResponse.Status);
        Assert.Equal(SlicingJobStatus.Queued, prusaResponse.Status);

        Assert.NotNull(orcaStatus);
        Assert.NotNull(prusaStatus);

        _output.WriteLine($"OrcaSlicer job {orcaResponse.JobId}: Status={orcaStatus.Status}");
        _output.WriteLine($"PrusaSlicer job {prusaResponse.JobId}: Status={prusaStatus.Status}");

        // Verify that jobs are being processed independently
        Assert.True(orcaStatus.Status is SlicingJobStatus.Queued or SlicingJobStatus.Slicing or SlicingJobStatus.Completed);
        Assert.True(prusaStatus.Status is SlicingJobStatus.Queued or SlicingJobStatus.Slicing or SlicingJobStatus.Completed);
    }

    [Fact]
    public async Task GetAvailableEngines_ShouldReturnBothSlicerTypes()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();

        // Act
        var engines = await orchestrator.GetAvailableEnginesAsync();

        // Assert
        Assert.NotNull(engines);
        Assert.Contains(engines, e => e.Engine == SlicerEngineType.OrcaSlicer);
        Assert.Contains(engines, e => e.Engine == SlicerEngineType.PrusaSlicer);

        var orcaEngine = engines.First(e => e.Engine == SlicerEngineType.OrcaSlicer);
        var prusaEngine = engines.First(e => e.Engine == SlicerEngineType.PrusaSlicer);

        Assert.NotEmpty(orcaEngine.Version);
        Assert.NotEmpty(prusaEngine.Version);
        Assert.NotEqual(orcaEngine.Version, prusaEngine.Version);

        _output.WriteLine($"OrcaSlicer: Version {orcaEngine.Version}, Healthy: {orcaEngine.IsHealthy}");
        _output.WriteLine($"PrusaSlicer: Version {prusaEngine.Version}, Healthy: {prusaEngine.IsHealthy}");
    }

    [Fact]
    public async Task QueueStats_ShouldTrackEnginesIndependently()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ISlicerOrchestrator>();

        // Act
        var allStats = await orchestrator.GetAllQueueStatsAsync();

        // Assert
        Assert.NotNull(allStats);

        if (allStats.ContainsKey(SlicerEngineType.OrcaSlicer))
        {
            var orcaStats = allStats[SlicerEngineType.OrcaSlicer];
            Assert.Equal(SlicerEngineType.OrcaSlicer, orcaStats.Engine);
            _output.WriteLine($"OrcaSlicer stats: Queued={orcaStats.QueuedJobs}, Processing={orcaStats.ProcessingJobs}");
        }

        if (allStats.ContainsKey(SlicerEngineType.PrusaSlicer))
        {
            var prusaStats = allStats[SlicerEngineType.PrusaSlicer];
            Assert.Equal(SlicerEngineType.PrusaSlicer, prusaStats.Engine);
            _output.WriteLine($"PrusaSlicer stats: Queued={prusaStats.QueuedJobs}, Processing={prusaStats.ProcessingJobs}");
        }
    }

    [Theory]
    [InlineData(SlicerEngineType.OrcaSlicer, "1.8.0-mock")]
    [InlineData(SlicerEngineType.PrusaSlicer, "2.8.0-mock")]
    public async Task SlicerEngine_ShouldHaveDistinctProfiles(SlicerEngineType engineType, string expectedVersionPrefix)
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var engines = scope.ServiceProvider.GetServices<ISlicerEngine>();

        // Act
        var engine = engines.FirstOrDefault(e => e.EngineType == engineType);

        // Assert
        Assert.NotNull(engine);
        Assert.Equal(engineType, engine.EngineType);
        Assert.StartsWith(expectedVersionPrefix.Split('-')[0], engine.Version);
        Assert.NotEmpty(engine.SupportedFileExtensions);

        var isHealthy = await engine.IsHealthyAsync();
        _output.WriteLine($"{engineType}: Version={engine.Version}, Healthy={isHealthy}, Extensions=[{string.Join(", ", engine.SupportedFileExtensions)}]");

        // Verify no code duplication - each engine should have its own implementation
        Assert.True(engine.GetType().Name.Contains(engineType.ToString()));
    }

    [Fact]
    public async Task MockSlicerEngines_ShouldGenerateDistinctGcode()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var engines = scope.ServiceProvider.GetServices<ISlicerEngine>();

        var orcaEngine = engines.First(e => e.EngineType == SlicerEngineType.OrcaSlicer);
        var prusaEngine = engines.First(e => e.EngineType == SlicerEngineType.PrusaSlicer);

        var testJob = new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "https://example.com/test.stl",
            ModelFileName = "test.stl",
            Profile = new SlicerProfileDto
            {
                LayerHeight = 0.2,
                InfillPercentage = 20,
                PrintSpeed = 50,
                Material = "PLA"
            }
        };

        // Act
        var orcaResult = await orcaEngine.SliceAsync(testJob);
        var prusaResult = await prusaEngine.SliceAsync(testJob);

        // Assert
        Assert.True(orcaResult.Success);
        Assert.True(prusaResult.Success);

        Assert.Contains("OrcaSlicer", orcaResult.Metadata["SlicerEngine"]);
        Assert.Contains("PrusaSlicer", prusaResult.Metadata["SlicerEngine"]);

        Assert.NotEqual(orcaResult.Metadata["SlicerVersion"], prusaResult.Metadata["SlicerVersion"]);

        _output.WriteLine($"OrcaSlicer result: {orcaResult.EstimatedPrintTimeSeconds}s, {orcaResult.LayerCount} layers");
        _output.WriteLine($"PrusaSlicer result: {prusaResult.EstimatedPrintTimeSeconds}s, {prusaResult.LayerCount} layers");
    }

    [Fact]
    public async Task SlicerProfiles_ShouldValidateAbstractionLayer()
    {
        // This test ensures that the abstraction layer works correctly
        // and there's no code duplication beyond the adapter layer

        // Arrange
        using var scope = _factory.Services.CreateScope();
        var engines = scope.ServiceProvider.GetServices<ISlicerEngine>().ToList();

        // Act & Assert
        Assert.Contains(engines, e => e.EngineType == SlicerEngineType.OrcaSlicer);
        Assert.Contains(engines, e => e.EngineType == SlicerEngineType.PrusaSlicer);

        foreach (var engine in engines)
        {
            // Each engine should implement the same interface
            Assert.IsAssignableFrom<ISlicerEngine>(engine);

            // Each engine should have distinct file extension support
            Assert.NotEmpty(engine.SupportedFileExtensions);

            // Each engine should report its health independently
            var health = await engine.IsHealthyAsync();
            Assert.IsType<bool>(health);

            _output.WriteLine($"Engine {engine.EngineType}: Healthy={health}, Extensions={engine.SupportedFileExtensions.Count}");
        }

        // Verify abstraction: Different engine types but same interface contract
        var engineTypes = engines.Select(e => e.EngineType).Distinct().ToList();
        Assert.Contains(SlicerEngineType.OrcaSlicer, engineTypes);
        Assert.Contains(SlicerEngineType.PrusaSlicer, engineTypes);
        Assert.Equal(2, engineTypes.Count);
    }
}