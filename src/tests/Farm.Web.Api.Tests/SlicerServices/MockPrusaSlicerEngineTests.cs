using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Unit tests for MockPrusaSlicerEngine
/// </summary>
public class MockPrusaSlicerEngineTests
{
    private readonly ITestOutputHelper _output;
    private readonly MockPrusaSlicerEngine _engine;
    private readonly MockSlicerOptions _options;

    public MockPrusaSlicerEngineTests(ITestOutputHelper output)
    {
        _output = output;
        _options = new MockSlicerOptions
        {
            ProcessingTimeSeconds = 2.0, // Faster for testing
            FailureRate = 0.0, // No random failures in tests
            InitialDelaySeconds = 0.1
        };

        var logger = new TestLogger<MockPrusaSlicerEngine>();
        _engine = new MockPrusaSlicerEngine(Options.Create(_options), logger);
    }

    [Fact]
    public void EngineType_ShouldBePrusaSlicer()
    {
        // Act & Assert
        Assert.Equal(SlicerEngineType.PrusaSlicer, _engine.EngineType);
    }

    [Fact]
    public void Version_ShouldContainPrusaSlicerVersion()
    {
        // Act & Assert
        Assert.Contains("2.8.0", _engine.Version);
        Assert.Contains("mock", _engine.Version);
    }

    [Fact]
    public void SupportedFileExtensions_ShouldIncludeCommonFormats()
    {
        // Act
        var extensions = _engine.SupportedFileExtensions;

        // Assert
        Assert.Contains(".stl", extensions);
        Assert.Contains(".obj", extensions);
        Assert.Contains(".3mf", extensions);
        Assert.Contains(".amf", extensions);
        Assert.Contains(".ply", extensions);
    }

    [Fact]
    public async Task IsHealthyAsync_ShouldReturnTrue_WhenHealthy()
    {
        // Act
        var isHealthy = await _engine.IsHealthyAsync();

        // Assert
        Assert.True(isHealthy);
    }

    [Fact]
    public async Task SliceAsync_ShouldCompleteSuccessfully_WithValidJob()
    {
        // Arrange
        var job = CreateTestJob();
        var progressUpdates = new List<SlicingProgressUpdate>();
        var progress = new Progress<SlicingProgressUpdate>(update => progressUpdates.Add(update));

        // Act
        var result = await _engine.SliceAsync(job, progress);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.ProcessingTimeSeconds > 0);
        Assert.True(result.OutputFileSizeBytes > 0);
        Assert.True(result.EstimatedPrintTimeSeconds > 0);
        Assert.True(result.EstimatedFilamentUsageGrams > 0);
        Assert.True(result.LayerCount > 0);

        // Verify PrusaSlicer-specific metadata
        Assert.Equal("PrusaSlicer", result.Metadata["SlicerEngine"]);
        Assert.Contains("2.8.0", result.Metadata["SlicerVersion"]);
        Assert.Equal("true", result.Metadata["MockedResult"]);

        // Verify progress updates were reported
        Assert.NotEmpty(progressUpdates);
        Assert.All(progressUpdates, update => Assert.Equal(job.Id, update.JobId));

        _output.WriteLine($"Slicing completed in {result.ProcessingTimeSeconds:F2}s");
        _output.WriteLine($"Output: {result.OutputFileSizeBytes} bytes, {result.LayerCount} layers");
        _output.WriteLine($"Estimated: {result.EstimatedPrintTimeSeconds:F0}s print time, {result.EstimatedFilamentUsageGrams:F1}g filament");
    }

    [Fact]
    public async Task SliceAsync_ShouldReportProgress_DuringProcessing()
    {
        // Arrange
        var job = CreateTestJob();
        var progressUpdates = new List<SlicingProgressUpdate>();
        var progress = new Progress<SlicingProgressUpdate>(update =>
        {
            progressUpdates.Add(update);
            _output.WriteLine($"Progress: {update.Progress}% - {update.CurrentStep}");
        });

        // Act
        var result = await _engine.SliceAsync(job, progress);

        // Assert
        Assert.True(result.Success);
        Assert.NotEmpty(progressUpdates);

        // Verify progress sequence
        Assert.Contains(progressUpdates, u => u.Progress == 0 && u.CurrentStep == "Loading model");
        Assert.Contains(progressUpdates, u => u.Progress == 100);

        // Verify progress is monotonically increasing
        for (int i = 1; i < progressUpdates.Count; i++)
        {
            Assert.True(progressUpdates[i].Progress >= progressUpdates[i - 1].Progress);
        }
    }

    [Fact]
    public async Task SliceAsync_ShouldGeneratePrusaSlicerSpecificGcode()
    {
        // Arrange
        var job = CreateTestJob();

        // Act
        var result = await _engine.SliceAsync(job);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("PrusaSlicer", result.Metadata["SlicerEngine"]);
        Assert.StartsWith("2.8.0", result.Metadata["SlicerVersion"]);

        // Verify profile information is included
        Assert.Equal("0.20", result.Metadata["LayerHeight"]);
        Assert.Equal("20", result.Metadata["InfillPercentage"]);
        Assert.Equal("50", result.Metadata["PrintSpeed"]);
        Assert.Equal("Standard - PLA", result.Metadata["ProfileUsed"]);
    }

    [Fact]
    public async Task SliceAsync_ShouldHandleCancellation()
    {
        // Arrange
        var job = CreateTestJob();
        using var cts = new CancellationTokenSource();

        // Act
        var sliceTask = _engine.SliceAsync(job, cancellationToken: cts.Token);
        cts.Cancel(); // Cancel immediately

        var result = await sliceTask;

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Job was cancelled", result.Error);
        Assert.Equal(0, result.ProcessingTimeSeconds);
    }

    [Fact]
    public async Task ValidateModelAsync_ShouldReturnValid_ForValidModel()
    {
        // Arrange
        var modelData = "Mock STL content for testing"u8.ToArray();
        using var modelStream = new MemoryStream(modelData);

        // Act
        var result = await _engine.ValidateModelAsync(modelStream);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.Equal(modelData.Length, result.FileSizeBytes);
        Assert.Equal("STL", result.FileType);
        Assert.Contains("PrusaSlicer-Mock", result.Metadata["ValidationEngine"].ToString());
    }

    [Fact]
    public async Task ValidateModelAsync_ShouldReturnInvalid_ForEmptyModel()
    {
        // Arrange
        using var emptyStream = new MemoryStream();

        // Act
        var result = await _engine.ValidateModelAsync(emptyStream);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains("File is empty", result.Issues);
        Assert.Equal(0, result.FileSizeBytes);
    }

    [Fact]
    public async Task EstimateProcessingTimeAsync_ShouldVaryByComplexity()
    {
        // Arrange
        var simpleJob = CreateTestJob(layerHeight: 0.3, infill: 10, supports: false);
        var complexJob = CreateTestJob(layerHeight: 0.1, infill: 50, supports: true);

        // Act
        var simpleTime = await _engine.EstimateProcessingTimeAsync(simpleJob);
        var complexTime = await _engine.EstimateProcessingTimeAsync(complexJob);

        // Assert
        Assert.True(simpleTime > TimeSpan.Zero);
        Assert.True(complexTime > TimeSpan.Zero);
        Assert.True(complexTime > simpleTime); // Complex jobs should take longer

        _output.WriteLine($"Simple job estimate: {simpleTime.TotalSeconds:F1}s");
        _output.WriteLine($"Complex job estimate: {complexTime.TotalSeconds:F1}s");
    }

    [Theory]
    [InlineData(0.1, 50, true)] // Fine layer, high infill, supports
    [InlineData(0.2, 20, false)] // Standard settings
    [InlineData(0.3, 10, false)] // Draft quality
    public async Task SliceAsync_ShouldAdjustResults_BasedOnProfile(double layerHeight, int infill, bool supports)
    {
        // Arrange
        var job = CreateTestJob(layerHeight, infill, supports);

        // Act
        var result = await _engine.SliceAsync(job);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.LayerCount > 0);
        Assert.True(result.EstimatedPrintTimeSeconds > 0);
        Assert.True(result.EstimatedFilamentUsageGrams > 0);

        _output.WriteLine($"Profile: {layerHeight}mm, {infill}% infill, supports: {supports}");
        _output.WriteLine($"Result: {result.LayerCount} layers, {result.EstimatedPrintTimeSeconds:F0}s, {result.EstimatedFilamentUsageGrams:F1}g");
    }

    private static DistributedSlicingJob CreateTestJob(double layerHeight = 0.2, int infill = 20, bool supports = false)
    {
        return new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "https://example.com/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.PrusaSlicer,
            Profile = new SlicerProfileDto
            {
                Quality = "Standard",
                Material = "PLA",
                LayerHeight = layerHeight,
                InfillPercentage = infill,
                PrintSpeed = 50,
                NozzleTemperature = 210,
                BedTemperature = 60,
                Supports = supports
            }
        };
    }
}

/// <summary>
/// Simple test logger implementation
/// </summary>
public class TestLogger<T> : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) => null!;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // No-op for tests
    }
}