using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Integration tests for the distributed slicer microservices system
/// Tests the interaction between orchestrator, engines, storage, and notifications
/// </summary>
public class SlicerServicesIntegrationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly string _tempStoragePath;
    private readonly Mock<ISlicerJobQueue> _mockJobQueue;
    private readonly Mock<ISlicerProgressNotifier> _mockProgressNotifier;

    public SlicerServicesIntegrationTests()
    {
        _tempStoragePath = Path.Combine(TestInfrastructure.TestPaths.GetUniqueTempDirectory(), "slicer-integration-tests");
        _mockJobQueue = new Mock<ISlicerJobQueue>();
        _mockProgressNotifier = new Mock<ISlicerProgressNotifier>();

        var services = new ServiceCollection();

        // Add logging
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

        // Configure file storage
        services.Configure<LocalFileStorageOptions>(options =>
        {
            options.BasePath = _tempStoragePath;
        });

        // Configure mock slicer with fast processing
        services.Configure<MockSlicerOptions>(options =>
        {
            options.InitialDelaySeconds = 0.1;
            options.ProcessingTimeSeconds = 0.5;
            options.FailureRate = 0.0; // No failures for integration tests
        });

        // Register services
        services.AddSingleton(_mockJobQueue.Object);
        services.AddSingleton(_mockProgressNotifier.Object);
        services.AddScoped<ISlicerFileStorage, LocalSlicerFileStorage>();
        services.AddScoped<ISlicerEngine, MockOrcaSlicerEngine>();
        services.AddScoped<ISlicerEngine, MockPrusaSlicerEngine>();
        services.AddScoped<ISlicerOrchestrator, SlicerOrchestrator>();

        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();

        if (Directory.Exists(_tempStoragePath))
        {
            Directory.Delete(_tempStoragePath, recursive: true);
        }
    }

    [Fact]
    public async Task EndToEndSlicingWorkflow_ValidRequest_ShouldCompleteSuccessfully()
    {
        // Arrange
        var orchestrator = _serviceProvider.GetRequiredService<ISlicerOrchestrator>();
        var fileStorage = _serviceProvider.GetRequiredService<ISlicerFileStorage>();
        var slicerEngine = _serviceProvider.GetRequiredService<ISlicerEngine>();

        // Upload a test model file
        var modelContent = CreateTestStlContent();
        var modelFileUrl = await fileStorage.UploadFileAsync("test-models/cube.stl", modelContent, "application/octet-stream");

        var request = new SlicingJobRequest
        {
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = modelFileUrl,
            ModelFileName = "cube.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfile = new SlicerProfileDto
            {
                LayerHeight = 0.2,
                InfillPercentage = 20,
                PrintSpeed = 50,
                NozzleTemperature = 210,
                BedTemperature = 60,
                Material = "PLA",
                Quality = "Standard"
            },
            Priority = SlicingJobPriority.Normal,
            Metadata = new Dictionary<string, object>
            {
                ["TestRun"] = true,
                ["ClientVersion"] = "1.0.0"
            }
        };

        // Setup mock queue to return the job when dequeued
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(It.IsAny<SlicerEngineType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerQueueStats
            {
                Engine = SlicerEngineType.OrcaSlicer,
                QueuedJobs = 1,
                EstimatedWaitTime = TimeSpan.FromMinutes(5)
            });

        // Act - Submit job
        var jobResponse = await orchestrator.SubmitJobAsync(request);

        // Assert - Job submission
        jobResponse.Should().NotBeNull();
        jobResponse.JobId.Should().NotBeEmpty();
        jobResponse.Status.Should().Be(SlicingJobStatus.Queued);
        jobResponse.SlicerWorkerUrl.Should().Contain("orcaslicer-service");

        // Verify job was enqueued
        _mockJobQueue.Verify(q => q.EnqueueAsync(
            It.Is<DistributedSlicingJob>(job =>
                job.UserId == request.UserId &&
                job.ModelFileUrl == modelFileUrl &&
                job.SlicerEngine == SlicerEngineType.OrcaSlicer &&
                job.Priority == SlicingJobPriority.Normal
            ),
            It.IsAny<CancellationToken>()
        ), Times.Once);

        // Verify file storage integration
        var fileExists = await fileStorage.FileExistsAsync(modelFileUrl);
        fileExists.Should().BeTrue();

        var downloadedContent = await fileStorage.DownloadFileBytesAsync(modelFileUrl);
        downloadedContent.Should().BeEquivalentTo(modelContent);
    }

    [Fact]
    public async Task SlicerEngineProcessing_WithProgressReporting_ShouldReportProgress()
    {
        // Arrange
        var slicerEngine = _serviceProvider.GetRequiredService<ISlicerEngine>();
        var fileStorage = _serviceProvider.GetRequiredService<ISlicerFileStorage>();

        var job = new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "test://model.stl",
            ModelFileName = "test-model.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Profile = new SlicerProfileDto
            {
                LayerHeight = 0.1, // Fine layer height for more layers
                InfillPercentage = 50,
                Material = "PETG"
            },
            Priority = SlicingJobPriority.High,
            Status = SlicingJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            InputFileSizeBytes = 2 * 1024 * 1024 // 2MB
        };

        var progressUpdates = new List<SlicingProgressUpdate>();
        var progress = new Progress<SlicingProgressUpdate>(update => progressUpdates.Add(update));

        // Act
        var result = await slicerEngine.SliceAsync(job, progress);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.ProcessingTimeSeconds.Should().BeGreaterThan(0);
        result.LayerCount.Should().BeGreaterThan(0);
        result.EstimatedPrintTimeSeconds.Should().BeGreaterThan(0);
        result.EstimatedFilamentUsageGrams.Should().BeGreaterThan(0);

        // Verify progress reporting
        progressUpdates.Should().NotBeEmpty();
        progressUpdates.Should().Contain(u => u.Progress == 0);
        progressUpdates.Should().Contain(u => u.Progress == 100);
        progressUpdates.Should().BeInAscendingOrder(u => u.Timestamp);
        progressUpdates.All(u => u.JobId == job.Id).Should().BeTrue();
        progressUpdates.All(u => u.Status == SlicingJobStatus.Slicing).Should().BeTrue();

        // Verify final result contains generated G-code metadata
        result.Metadata.Should().ContainKey("GeneratedGcode");
        result.Metadata["GeneratedGcode"].ToString().Should().Contain("; Generated by MockOrcaSlicerEngine");
    }

    [Fact]
    public async Task FileStorageIntegration_CompleteWorkflow_ShouldHandleAllFileOperations()
    {
        // Arrange
        var fileStorage = _serviceProvider.GetRequiredService<ISlicerFileStorage>();
        var modelContent = CreateTestStlContent();
        var gcodeContent = CreateTestGcodeContent();

        // Act - Upload model file
        var modelKey = "integration-test/model.stl";
        var modelUrl = await fileStorage.UploadFileAsync(modelKey, modelContent, "application/octet-stream");

        // Verify upload
        var modelExists = await fileStorage.FileExistsAsync(modelKey);
        var modelMetadata = await fileStorage.GetFileMetadataAsync(modelKey);

        // Download and verify model
        var downloadedModel = await fileStorage.DownloadFileBytesAsync(modelKey);

        // Upload G-code result
        var gcodeKey = "integration-test/result.gcode";
        var gcodeUrl = await fileStorage.UploadFileAsync(gcodeKey, gcodeContent, "text/plain");

        // Generate signed URLs
        var modelSignedUrl = await fileStorage.GenerateSignedUrlAsync(modelKey, TimeSpan.FromHours(1));
        var gcodeSignedUrl = await fileStorage.GenerateSignedUrlAsync(gcodeKey, TimeSpan.FromHours(1));

        // Assert
        modelUrl.Should().NotBeNullOrEmpty();
        gcodeUrl.Should().NotBeNullOrEmpty();

        modelExists.Should().BeTrue();
        modelMetadata.Should().NotBeNull();
        modelMetadata!.SizeBytes.Should().Be(modelContent.Length);
        modelMetadata.ContentType.Should().Be("application/octet-stream");

        downloadedModel.Should().BeEquivalentTo(modelContent);

        modelSignedUrl.Should().Contain(modelKey);
        gcodeSignedUrl.Should().Contain(gcodeKey);

        // Cleanup test
        await fileStorage.DeleteFileAsync(modelKey);
        await fileStorage.DeleteFileAsync(gcodeKey);

        var modelExistsAfterDelete = await fileStorage.FileExistsAsync(modelKey);
        var gcodeExistsAfterDelete = await fileStorage.FileExistsAsync(gcodeKey);

        modelExistsAfterDelete.Should().BeFalse();
        gcodeExistsAfterDelete.Should().BeFalse();
    }

    [Fact]
    public async Task OrchestratorValidation_InvalidRequests_ShouldRejectAppropriately()
    {
        // Arrange
        var orchestrator = _serviceProvider.GetRequiredService<ISlicerOrchestrator>();

        // Test invalid user ID
        var invalidUserRequest = CreateValidSlicingJobRequest();
        invalidUserRequest.UserId = Guid.Empty;

        // Test invalid printer ID  
        var invalidPrinterRequest = CreateValidSlicingJobRequest();
        invalidPrinterRequest.PrinterId = Guid.Empty;

        // Test empty model file URL
        var emptyModelRequest = CreateValidSlicingJobRequest();
        emptyModelRequest.ModelFileUrl = "";

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.SubmitJobAsync(invalidUserRequest));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.SubmitJobAsync(invalidPrinterRequest));
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.SubmitJobAsync(emptyModelRequest));
    }

    [Fact]
    public async Task SlicerEngineValidation_ModelValidation_ShouldWorkCorrectly()
    {
        // Arrange
        var slicerEngine = _serviceProvider.GetRequiredService<ISlicerEngine>();

        // Test valid model
        var validModelContent = CreateTestStlContent();
        using var validStream = new MemoryStream(validModelContent);

        // Test empty model
        using var emptyStream = new MemoryStream();

        // Test oversized model
        var oversizedContent = new byte[60 * 1024 * 1024]; // 60MB
        using var oversizedStream = new MemoryStream(oversizedContent);

        // Act
        var validResult = await slicerEngine.ValidateModelAsync(validStream);
        var emptyResult = await slicerEngine.ValidateModelAsync(emptyStream);
        var oversizedResult = await slicerEngine.ValidateModelAsync(oversizedStream);

        // Assert
        validResult.IsValid.Should().BeTrue();
        validResult.Issues.Should().BeEmpty();

        emptyResult.IsValid.Should().BeFalse();
        emptyResult.Issues.Should().Contain("File is empty");

        oversizedResult.Warnings.Should().Contain(w => w.Contains("large file"));
    }

    [Fact]
    public async Task ProcessingTimeEstimation_DifferentComplexities_ShouldVaryAppropriately()
    {
        // Arrange
        var slicerEngine = _serviceProvider.GetRequiredService<ISlicerEngine>();

        var simpleJob = CreateDistributedSlicingJob();
        simpleJob.Profile = new SlicerProfileDto { LayerHeight = 0.3, InfillPercentage = 10 };
        simpleJob.InputFileSizeBytes = 1024 * 1024; // 1MB

        var complexJob = CreateDistributedSlicingJob();
        complexJob.Profile = new SlicerProfileDto { LayerHeight = 0.1, InfillPercentage = 80 };
        complexJob.InputFileSizeBytes = 10 * 1024 * 1024; // 10MB

        // Act
        var simpleEstimate = await slicerEngine.EstimateProcessingTimeAsync(simpleJob);
        var complexEstimate = await slicerEngine.EstimateProcessingTimeAsync(complexJob);

        // Assert
        complexEstimate.Should().BeGreaterThan(simpleEstimate);
        simpleEstimate.Should().BeGreaterThan(TimeSpan.FromSeconds(10));
        complexEstimate.Should().BeLessThan(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task HealthMonitoring_SystemHealth_ShouldReportCorrectly()
    {
        // Arrange
        var orchestrator = _serviceProvider.GetRequiredService<ISlicerOrchestrator>();
        var slicerEngine = _serviceProvider.GetRequiredService<ISlicerEngine>();

        // Setup mock queue stats
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(It.IsAny<SlicerEngineType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerQueueStats
            {
                Engine = SlicerEngineType.OrcaSlicer,
                QueuedJobs = 3,
                ProcessingJobs = 1,
                ActiveWorkers = 2,
                EstimatedWaitTime = TimeSpan.FromMinutes(15)
            });

        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerQueueStats());

        // Act
        var orchestratorHealth = await orchestrator.GetHealthAsync();
        var engineHealthy = await slicerEngine.IsHealthyAsync();
        var availableEngines = await orchestrator.GetAvailableEnginesAsync();

        // Assert
        orchestratorHealth.Should().NotBeNull();
        orchestratorHealth.IsHealthy.Should().BeTrue();
        orchestratorHealth.JobQueueHealthy.Should().BeTrue();
        orchestratorHealth.FileStorageHealthy.Should().BeTrue();

        engineHealthy.Should().BeTrue();

        availableEngines.Should().ContainSingle();
        availableEngines.First().Engine.Should().Be(SlicerEngineType.OrcaSlicer);
        availableEngines.First().IsHealthy.Should().BeTrue();
        availableEngines.First().QueueDepth.Should().Be(3);
    }

    [Theory]
    [InlineData("PLA", 210, 60)]
    [InlineData("PETG", 230, 80)]
    [InlineData("ABS", 240, 100)]
    public async Task MaterialSpecificSlicing_ShouldGenerateCorrectSettings(string material, int nozzleTemp, int bedTemp)
    {
        // Arrange
        var slicerEngine = _serviceProvider.GetRequiredService<ISlicerEngine>();

        var job = CreateDistributedSlicingJob();
        job.Profile = new SlicerProfileDto
        {
            LayerHeight = 0.2,
            InfillPercentage = 20,
            Material = material,
            NozzleTemperature = nozzleTemp,
            BedTemperature = bedTemp
        };

        // Act
        var result = await slicerEngine.SliceAsync(job);

        // Assert
        result.Success.Should().BeTrue();
        result.Metadata.Should().ContainKey("GeneratedGcode");

        var gcode = result.Metadata["GeneratedGcode"].ToString();
        gcode.Should().Contain($"M104 S{nozzleTemp}"); // Nozzle temperature
        gcode.Should().Contain($"M140 S{bedTemp}"); // Bed temperature
        gcode.Should().Contain($"; Material: {material}");
    }

    // Helper methods

    private static SlicingJobRequest CreateValidSlicingJobRequest()
    {
        return new SlicingJobRequest
        {
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "https://storage.example.com/models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            SlicerProfile = new SlicerProfileDto
            {
                LayerHeight = 0.2,
                InfillPercentage = 20,
                Material = "PLA",
                Quality = "Standard"
            },
            Priority = SlicingJobPriority.Normal,
            Metadata = new Dictionary<string, object>()
        };
    }

    private static DistributedSlicingJob CreateDistributedSlicingJob()
    {
        return new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "test://model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Profile = new SlicerProfileDto
            {
                LayerHeight = 0.2,
                InfillPercentage = 20,
                Material = "PLA"
            },
            Priority = SlicingJobPriority.Normal,
            Status = SlicingJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            InputFileSizeBytes = 2 * 1024 * 1024
        };
    }

    private static byte[] CreateTestStlContent()
    {
        var content = """
            solid test_cube
              facet normal 0 0 1
                outer loop
                  vertex 0 0 1
                  vertex 1 0 1
                  vertex 1 1 1
                endloop
              endfacet
              facet normal 0 0 1
                outer loop
                  vertex 0 0 1
                  vertex 1 1 1
                  vertex 0 1 1
                endloop
              endfacet
              facet normal 0 0 -1
                outer loop
                  vertex 0 0 0
                  vertex 1 1 0
                  vertex 1 0 0
                endloop
              endfacet
            endsolid test_cube
            """;
        return System.Text.Encoding.ASCII.GetBytes(content);
    }

    private static byte[] CreateTestGcodeContent()
    {
        var content = """
            ; Generated by Integration Test
            ; Total print time: 1h 30m
            ; Filament used: 25.5g
            
            M104 S210 ; Set nozzle temperature
            M140 S60  ; Set bed temperature
            
            G28       ; Home all axes
            G1 Z0.2   ; Move to first layer height
            
            ; Start printing
            G1 X10 Y10 E5 F1800
            G1 X90 Y10 E25 F1800
            G1 X90 Y90 E45 F1800
            G1 X10 Y90 E65 F1800
            
            M104 S0   ; Turn off nozzle
            M140 S0   ; Turn off bed
            M84       ; Disable motors
            """;
        return System.Text.Encoding.UTF8.GetBytes(content);
    }
}
