using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Unit tests for MockOrcaSlicerEngine - Mock implementation for development and testing
/// </summary>
public class MockOrcaSlicerEngineTests
{
    private readonly Mock<ILogger<MockOrcaSlicerEngine>> _mockLogger;
    private readonly MockOrcaSlicerEngine _engine;
    private readonly MockSlicerOptions _options;

    public MockOrcaSlicerEngineTests()
    {
        _mockLogger = new Mock<ILogger<MockOrcaSlicerEngine>>();
        _options = new MockSlicerOptions
        {
            InitialDelaySeconds = 0.1, // Very short delays for testing
            ProcessingTimeSeconds = 0.5,
            FailureRate = 0.0 // No random failures in tests by default
        };

        var optionsWrapper = Options.Create(_options);
        _engine = new MockOrcaSlicerEngine(optionsWrapper, _mockLogger.Object);
    }

    [Fact]
    public void EngineType_ShouldReturnOrcaSlicer()
    {
        // Act & Assert
        _engine.EngineType.Should().Be(SlicerEngineType.OrcaSlicer);
    }

    [Fact]
    public void Version_ShouldReturnMockVersion()
    {
        // Act & Assert
        _engine.Version.Should().Be("1.8.0-mock");
    }

    [Fact]
    public void SupportedFileExtensions_ShouldReturnExpectedExtensions()
    {
        // Act & Assert
        _engine.SupportedFileExtensions.Should().Contain(".stl");
        _engine.SupportedFileExtensions.Should().Contain(".obj");
        _engine.SupportedFileExtensions.Should().Contain(".3mf");
        _engine.SupportedFileExtensions.Should().Contain(".amf");
        _engine.SupportedFileExtensions.Should().Contain(".ply");
        _engine.SupportedFileExtensions.Should().HaveCount(5);
    }

    [Fact]
    public async Task IsHealthyAsync_ShouldReturnTrue_MostOfTheTime()
    {
        // Act - Test multiple times since there's a small chance of random failure
        var healthResults = new List<bool>();
        for (int i = 0; i < 10; i++)
        {
            healthResults.Add(await _engine.IsHealthyAsync());
        }

        // Assert - Should be mostly healthy (allow for some random failures)
        var healthyCount = healthResults.Count(h => h);
        healthyCount.Should().BeGreaterThan(7); // At least 80% healthy
    }

    [Fact]
    public async Task IsHealthyAsync_WithCancellation_ShouldRespectCancellationToken()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(() => _engine.IsHealthyAsync(cts.Token));
    }

    [Fact]
    public async Task SliceAsync_ValidJob_ShouldReturnSuccessResult()
    {
        // Arrange
        var job = CreateValidSlicingJob();
        var progressUpdates = new List<SlicingProgressUpdate>();
        var progress = new Progress<SlicingProgressUpdate>(update => progressUpdates.Add(update));

        // Act
        var result = await _engine.SliceAsync(job, progress);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Error.Should().BeNullOrEmpty();
        result.ProcessingTimeSeconds.Should().BeGreaterThan(0);
        result.LayerCount.Should().BeGreaterThan(0);
        result.EstimatedPrintTimeSeconds.Should().BeGreaterThan(0);
        result.EstimatedFilamentUsageGrams.Should().BeGreaterThan(0);
        result.OutputFileSizeBytes.Should().BeGreaterThan(0);

        // Verify progress updates
        progressUpdates.Should().NotBeEmpty();
        progressUpdates.First().Progress.Should().Be(0);
        progressUpdates.Last().Progress.Should().Be(100);
        progressUpdates.Should().Contain(u => u.CurrentStep == "Validating model");
    // Allow for nullable CurrentStep; ensure at least one update has the slicing layer step
    progressUpdates.Should().Contain(u => u.CurrentStep != null && u.CurrentStep.Contains("Slicing layer"));
    }

    [Fact]
    public async Task SliceAsync_WithSimulatedFailure_ShouldReturnFailureResult()
    {
        // Arrange
        var failureOptions = new MockSlicerOptions
        {
            InitialDelaySeconds = 0.1,
            ProcessingTimeSeconds = 0.5,
            FailureRate = 1.0 // Force failure
        };

        var failingEngine = new MockOrcaSlicerEngine(Options.Create(failureOptions), _mockLogger.Object);
        var job = CreateValidSlicingJob();

        // Act
        var result = await failingEngine.SliceAsync(job);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        result.Error.Should().Contain("Simulated slicing failure");
    }

    [Fact]
    public async Task SliceAsync_WithCancellation_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var job = CreateValidSlicingJob();
        var cts = new CancellationTokenSource();
        
        // Cancel after a very short delay to test cancellation during processing
        _ = Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None).ContinueWith(_ => cts.Cancel(), TaskScheduler.Default);

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => _engine.SliceAsync(job, null, cts.Token));
    }

    [Fact]
    public async Task SliceAsync_WithProgressCallback_ShouldReportProgress()
    {
        // Arrange
        var job = CreateValidSlicingJob();
        var progressUpdates = new List<SlicingProgressUpdate>();
        var progress = new Progress<SlicingProgressUpdate>(update => progressUpdates.Add(update));

        // Act
        await _engine.SliceAsync(job, progress);

        // Assert
        progressUpdates.Should().HaveCountGreaterThan(10); // Should have multiple progress updates
        progressUpdates.Should().BeInAscendingOrder(u => u.Progress);
        progressUpdates.All(u => u.JobId == job.Id).Should().BeTrue();
        progressUpdates.All(u => u.Status == SlicingJobStatus.Slicing).Should().BeTrue();
        progressUpdates.All(u => u.Timestamp > DateTime.MinValue).Should().BeTrue();
    }

    [Theory]
    [InlineData(0.1, "Draft")]
    [InlineData(0.2, "Standard")]
    [InlineData(0.3, "Fine")]
    public async Task SliceAsync_DifferentLayerHeights_ShouldAffectProcessingTime(double layerHeight, string quality)
    {
        // Arrange
        var job = CreateValidSlicingJob();
        job.Profile = new SlicerProfileDto
        {
            LayerHeight = layerHeight,
            Quality = quality,
            Material = "PLA"
        };

        // Act
        var result = await _engine.SliceAsync(job);

        // Assert
        result.Success.Should().BeTrue();
        result.LayerCount.Should().BeGreaterThan(0);
        
        // Finer layer heights should result in more layers
        if (layerHeight <= 0.1)
        {
            result.LayerCount.Should().BeGreaterThan(200);
        }
    }

    [Theory]
    [InlineData("PLA", 190, 60)]
    [InlineData("PETG", 230, 80)]
    [InlineData("ABS", 240, 100)]
    public async Task SliceAsync_DifferentMaterials_ShouldGenerateAppropriateGCode(string material, int expectedNozzleTemp, int expectedBedTemp)
    {
        // Arrange
        var job = CreateValidSlicingJob();
        job.Profile = new SlicerProfileDto
        {
            LayerHeight = 0.2,
            Material = material,
            NozzleTemperature = expectedNozzleTemp,
            BedTemperature = expectedBedTemp
        };

        // Act
        var result = await _engine.SliceAsync(job);

        // Assert
        result.Success.Should().BeTrue();
        result.Metadata.Should().ContainKey("GeneratedGcode");
        
        // Mock G-code should contain temperature settings
        var gcode = result.Metadata["GeneratedGcode"].ToString();
        gcode.Should().Contain($"M104 S{expectedNozzleTemp}"); // Nozzle temperature
        gcode.Should().Contain($"M140 S{expectedBedTemp}"); // Bed temperature
        gcode.Should().Contain($"; Material: {material}");
    }

    [Fact]
    public async Task ValidateModelAsync_ValidStream_ShouldReturnValidResult()
    {
        // Arrange
        var modelContent = CreateValidStlContent();
        using var stream = new MemoryStream(modelContent);

        // Act
        var result = await _engine.ValidateModelAsync(stream);

        // Assert
        result.Should().NotBeNull();
        result.IsValid.Should().BeTrue();
        result.Issues.Should().BeEmpty();
        result.FileSizeBytes.Should().Be(modelContent.Length);
        result.FileType.Should().Be("STL");
        result.Metadata.Should().ContainKey("TriangleCount");
        result.Metadata.Should().ContainKey("HasManifoldErrors");
    }

    [Fact]
    public async Task ValidateModelAsync_EmptyStream_ShouldReturnInvalidResult()
    {
        // Arrange
        using var stream = new MemoryStream();

        // Act
        var result = await _engine.ValidateModelAsync(stream);

        // Assert
        result.Should().NotBeNull();
        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain("File is empty");
        result.FileSizeBytes.Should().Be(0);
    }

    [Fact]
    public async Task ValidateModelAsync_TooLargeFile_ShouldReturnWarning()
    {
        // Arrange
        var largeContent = new byte[60 * 1024 * 1024]; // 60MB
        using var stream = new MemoryStream(largeContent);

        // Act
        var result = await _engine.ValidateModelAsync(stream);

        // Assert
        result.Should().NotBeNull();
        result.Warnings.Should().Contain(w => w.Contains("large file"));
    }

    [Fact]
    public async Task EstimateProcessingTimeAsync_ShouldReturnReasonableEstimate()
    {
        // Arrange
        var job = CreateValidSlicingJob();
        
        // Act
        var result = await _engine.EstimateProcessingTimeAsync(job);

        // Assert
        result.Should().BeGreaterThan(TimeSpan.FromSeconds(30));
        result.Should().BeLessThan(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task EstimateProcessingTimeAsync_ComplexModel_ShouldTakeLonger()
    {
        // Arrange
        var simpleJob = CreateValidSlicingJob();
        simpleJob.Profile = new SlicerProfileDto { LayerHeight = 0.3, InfillPercentage = 10 };
        
        var complexJob = CreateValidSlicingJob();
        complexJob.Profile = new SlicerProfileDto { LayerHeight = 0.1, InfillPercentage = 80 };

        // Act
        var simpleTime = await _engine.EstimateProcessingTimeAsync(simpleJob);
        var complexTime = await _engine.EstimateProcessingTimeAsync(complexJob);

        // Assert
        complexTime.Should().BeGreaterThan(simpleTime);
    }

    [Fact]
    public async Task EstimateProcessingTimeAsync_WithFileSize_ShouldScaleWithSize()
    {
        // Arrange
        var smallJob = CreateValidSlicingJob();
        smallJob.InputFileSizeBytes = 1024 * 1024; // 1MB
        
        var largeJob = CreateValidSlicingJob();
        largeJob.InputFileSizeBytes = 10 * 1024 * 1024; // 10MB

        // Act
        var smallTime = await _engine.EstimateProcessingTimeAsync(smallJob);
        var largeTime = await _engine.EstimateProcessingTimeAsync(largeJob);

        // Assert
        largeTime.Should().BeGreaterThan(smallTime);
    }

    // Helper methods

    private static DistributedSlicingJob CreateValidSlicingJob()
    {
        return new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "https://storage.example.com/models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Profile = new SlicerProfileDto
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
            Status = SlicingJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            InputFileSizeBytes = 2 * 1024 * 1024 // 2MB
        };
    }

    private static byte[] CreateValidStlContent()
    {
        // Create a minimal valid STL file (ASCII format)
        var stlContent = """
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
            endsolid test_cube
            """;
        return System.Text.Encoding.ASCII.GetBytes(stlContent);
    }
}

/// <summary>
/// Configuration options for MockOrcaSlicerEngine
/// </summary>
// NOTE: Removed duplicate MockSlicerOptions test shim.
// The production options class (Farm.Web.Api.Services.SlicerServices.MockSlicerOptions)
// is referenced via using at top of file.