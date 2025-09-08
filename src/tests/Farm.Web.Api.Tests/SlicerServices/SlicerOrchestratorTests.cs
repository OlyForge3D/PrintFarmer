using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Unit tests for SlicerOrchestrator - Central coordination service for distributed slicing operations
/// </summary>
public class SlicerOrchestratorTests
{
    private readonly Mock<ISlicerJobQueue> _mockJobQueue;
    private readonly Mock<ISlicerFileStorage> _mockFileStorage;
    private readonly Mock<ISlicerProgressNotifier> _mockProgressNotifier;
    private readonly Mock<ISlicerEngine> _mockOrcaSlicerEngine;
    private readonly Mock<ISlicerEngine> _mockPrusaSlicerEngine;
    private readonly Mock<ILogger<SlicerOrchestrator>> _mockLogger;
    private readonly SlicerOrchestrator _orchestrator;

    public SlicerOrchestratorTests()
    {
        _mockJobQueue = new Mock<ISlicerJobQueue>();
        _mockFileStorage = new Mock<ISlicerFileStorage>();
        _mockProgressNotifier = new Mock<ISlicerProgressNotifier>();
        _mockOrcaSlicerEngine = new Mock<ISlicerEngine>();
        _mockPrusaSlicerEngine = new Mock<ISlicerEngine>();
        _mockLogger = new Mock<ILogger<SlicerOrchestrator>>();

        // Setup mock engines
        _mockOrcaSlicerEngine.Setup(e => e.EngineType).Returns(SlicerEngineType.OrcaSlicer);
        _mockOrcaSlicerEngine.Setup(e => e.Version).Returns("1.8.0-test");
        _mockOrcaSlicerEngine.Setup(e => e.SupportedFileExtensions).Returns(new[] { ".stl", ".obj", ".3mf" });

        _mockPrusaSlicerEngine.Setup(e => e.EngineType).Returns(SlicerEngineType.PrusaSlicer);
        _mockPrusaSlicerEngine.Setup(e => e.Version).Returns("2.7.0-test");
        _mockPrusaSlicerEngine.Setup(e => e.SupportedFileExtensions).Returns(new[] { ".stl", ".obj", ".amf" });

        var engines = new[] { _mockOrcaSlicerEngine.Object, _mockPrusaSlicerEngine.Object };

        _orchestrator = new SlicerOrchestrator(
            _mockJobQueue.Object,
            _mockFileStorage.Object,
            _mockProgressNotifier.Object,
            engines,
            _mockLogger.Object);
    }

    [Fact]
    public async Task SubmitJobAsync_ValidRequest_ShouldEnqueueJobAndReturnResponse()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        var queueStats = new SlicerQueueStats
        {
            QueuedJobs = 5,
            EstimatedWaitTime = TimeSpan.FromMinutes(15)
        };

        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.FileExistsAsync(request.ModelFileUrl, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.GetFileMetadataAsync(request.ModelFileUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerFileMetadata { SizeBytes = 1024 * 1024 }); // 1MB
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(request.SlicerEngine, It.IsAny<CancellationToken>())).ReturnsAsync(queueStats);

        // Act
        var result = await _orchestrator.SubmitJobAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.JobId.Should().NotBeEmpty();
        result.Status.Should().Be(SlicingJobStatus.Queued);
        result.QueuePosition.Should().Be(5);
        result.SlicerWorkerUrl.Should().Be("http://orcaslicer-service:8080");

        _mockJobQueue.Verify(q => q.EnqueueAsync(It.Is<DistributedSlicingJob>(job =>
            job.UserId == request.UserId &&
            job.PrinterId == request.PrinterId &&
            job.ModelFileUrl == request.ModelFileUrl &&
            job.SlicerEngine == request.SlicerEngine &&
            job.Priority == request.Priority &&
            job.Status == SlicingJobStatus.Queued
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitJobAsync_EmptyUserId_ShouldThrowArgumentException()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        request.UserId = Guid.Empty;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _orchestrator.SubmitJobAsync(request));
        exception.Message.Should().Contain("UserId is required");
    }

    [Fact]
    public async Task SubmitJobAsync_EmptyPrinterId_ShouldThrowArgumentException()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        request.PrinterId = Guid.Empty;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _orchestrator.SubmitJobAsync(request));
        exception.Message.Should().Contain("PrinterId is required");
    }

    [Fact]
    public async Task SubmitJobAsync_EmptyModelFileUrl_ShouldThrowArgumentException()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        request.ModelFileUrl = "";

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _orchestrator.SubmitJobAsync(request));
        exception.Message.Should().Contain("ModelFileUrl is required");
    }

    [Fact]
    public async Task SubmitJobAsync_UnsupportedSlicerEngine_ShouldThrowArgumentException()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        request.SlicerEngine = SlicerEngineType.Cura; // Not supported in our setup

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _orchestrator.SubmitJobAsync(request));
        exception.Message.Should().Contain("Slicer engine Cura is not available");
    }

    [Fact]
    public async Task SubmitJobAsync_UnhealthySlicerEngine_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _orchestrator.SubmitJobAsync(request));
        exception.Message.Should().Contain("Slicer engine OrcaSlicer is currently unavailable");
    }

    [Fact]
    public async Task SubmitJobAsync_FileNotFound_ShouldThrowFileNotFoundException()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.FileExistsAsync(request.ModelFileUrl, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => _orchestrator.SubmitJobAsync(request));
        exception.Message.Should().Contain("Model file not found");
    }

    [Fact]
    public async Task SubmitJobAsync_UnsupportedFileExtension_ShouldThrowArgumentException()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        request.ModelFileName = "test.xyz"; // Unsupported extension

        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.FileExistsAsync(request.ModelFileUrl, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _orchestrator.SubmitJobAsync(request));
        exception.Message.Should().Contain("File extension .xyz is not supported");
    }

    [Fact]
    public async Task SubmitJobAsync_FileTooLarge_ShouldThrowArgumentException()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.FileExistsAsync(request.ModelFileUrl, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.GetFileMetadataAsync(request.ModelFileUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerFileMetadata { SizeBytes = 150_000_000 }); // 150MB - exceeds 100MB limit

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _orchestrator.SubmitJobAsync(request));
        exception.Message.Should().Contain("File size exceeds maximum limit of 100MB");
    }

    [Fact]
    public async Task GetJobStatusAsync_ExistingJob_ShouldReturnJobStatus()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var job = CreateDistributedSlicingJob(jobId);
        job.Status = SlicingJobStatus.Slicing;
        job.Progress = 50;

        _mockJobQueue.Setup(q => q.GetJobAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        // Act
        var result = await _orchestrator.GetJobStatusAsync(jobId);

        // Assert
        result.Should().NotBeNull();
        result!.JobId.Should().Be(jobId);
        result.Status.Should().Be(SlicingJobStatus.Slicing);
        result.Progress.Should().Be(50);
    }

    [Fact]
    public async Task GetJobStatusAsync_NonExistentJob_ShouldReturnNull()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _mockJobQueue.Setup(q => q.GetJobAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync((DistributedSlicingJob?)null);

        // Act
        var result = await _orchestrator.GetJobStatusAsync(jobId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task CancelJobAsync_ExistingJob_ShouldCancelJobAndReturnTrue()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var job = CreateDistributedSlicingJob(jobId);
        job.Status = SlicingJobStatus.Queued;

        _mockJobQueue.Setup(q => q.GetJobAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        // Act
        var result = await _orchestrator.CancelJobAsync(jobId);

        // Assert
        result.Should().BeTrue();
        _mockJobQueue.Verify(q => q.CancelJobAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        _mockProgressNotifier.Verify(p => p.NotifyFailureAsync(job, "Job cancelled by user", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelJobAsync_NonExistentJob_ShouldReturnFalse()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        _mockJobQueue.Setup(q => q.GetJobAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync((DistributedSlicingJob?)null);

        // Act
        var result = await _orchestrator.CancelJobAsync(jobId);

        // Assert
        result.Should().BeFalse();
        _mockJobQueue.Verify(q => q.CancelJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelJobAsync_AlreadyCompletedJob_ShouldReturnFalse()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var job = CreateDistributedSlicingJob(jobId);
        job.Status = SlicingJobStatus.Completed;

        _mockJobQueue.Setup(q => q.GetJobAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        // Act
        var result = await _orchestrator.CancelJobAsync(jobId);

        // Assert
        result.Should().BeFalse();
        _mockJobQueue.Verify(q => q.CancelJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetAvailableEnginesAsync_ShouldReturnEngineInfoWithStats()
    {
        // Arrange
        var orcaQueueStats = new SlicerQueueStats
        {
            QueuedJobs = 3,
            ActiveWorkers = 2,
            EstimatedWaitTime = TimeSpan.FromMinutes(10)
        };

        var prusaQueueStats = new SlicerQueueStats
        {
            QueuedJobs = 1,
            ActiveWorkers = 1,
            EstimatedWaitTime = TimeSpan.FromMinutes(5)
        };

        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(SlicerEngineType.OrcaSlicer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcaQueueStats);
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(SlicerEngineType.PrusaSlicer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(prusaQueueStats);

        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockPrusaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        // Act
        var result = await _orchestrator.GetAvailableEnginesAsync();

        // Assert
        result.Should().HaveCount(2);

        var orcaEngine = result.First(e => e.Engine == SlicerEngineType.OrcaSlicer);
        orcaEngine.Version.Should().Be("1.8.0-test");
        orcaEngine.IsHealthy.Should().BeTrue();
        orcaEngine.QueueDepth.Should().Be(3);
        orcaEngine.ActiveWorkers.Should().Be(2);

        var prusaEngine = result.First(e => e.Engine == SlicerEngineType.PrusaSlicer);
        prusaEngine.Version.Should().Be("2.7.0-test");
        prusaEngine.IsHealthy.Should().BeFalse();
        prusaEngine.QueueDepth.Should().Be(1);
    }

    [Fact]
    public async Task GetAllQueueStatsAsync_ShouldReturnStatsForAllEngines()
    {
        // Arrange
        var orcaQueueStats = new SlicerQueueStats { Engine = SlicerEngineType.OrcaSlicer, QueuedJobs = 5 };
        var prusaQueueStats = new SlicerQueueStats { Engine = SlicerEngineType.PrusaSlicer, QueuedJobs = 2 };

        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(SlicerEngineType.OrcaSlicer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(orcaQueueStats);
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(SlicerEngineType.PrusaSlicer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(prusaQueueStats);

        // Act
        var result = await _orchestrator.GetAllQueueStatsAsync();

        // Assert
        result.Should().HaveCount(2);
        result[SlicerEngineType.OrcaSlicer].QueuedJobs.Should().Be(5);
        result[SlicerEngineType.PrusaSlicer].QueuedJobs.Should().Be(2);
    }

    [Fact]
    public async Task GetUserJobsAsync_ShouldReturnUserJobs()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var job1 = CreateDistributedSlicingJob(Guid.NewGuid());
        job1.UserId = userId;
        job1.Status = SlicingJobStatus.Completed;
        var job2 = CreateDistributedSlicingJob(Guid.NewGuid());
        job2.UserId = userId;
        job2.Status = SlicingJobStatus.Slicing;
        var jobs = new List<DistributedSlicingJob> { job1, job2 };

        _mockJobQueue.Setup(q => q.GetUserJobsAsync(userId, null, It.IsAny<CancellationToken>())).ReturnsAsync(jobs);

        // Act
        var result = await _orchestrator.GetUserJobsAsync(userId);

        // Assert
        result.Should().HaveCount(2);
        result.TrueForAll(j => j.JobId != Guid.Empty).Should().BeTrue();
    }

    [Fact]
    public async Task GetHealthAsync_AllHealthy_ShouldReturnHealthyStatus()
    {
        // Arrange
        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockPrusaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var queueStats = new SlicerQueueStats { QueuedJobs = 2, ActiveWorkers = 1 };
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(It.IsAny<SlicerEngineType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueStats);
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueStats);

        _mockFileStorage.Setup(f => f.FileExistsAsync("health-check-non-existent-file", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _orchestrator.GetHealthAsync();

        // Assert
        result.IsHealthy.Should().BeTrue();
        result.JobQueueHealthy.Should().BeTrue();
        result.FileStorageHealthy.Should().BeTrue();
        result.TotalQueuedJobs.Should().Be(4); // 2 engines x 2 jobs each
        result.TotalActiveJobs.Should().Be(2); // 2 engines x 1 worker each
        result.Engines.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetHealthAsync_UnhealthyEngine_ShouldReturnUnhealthyStatus()
    {
        // Arrange
        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _mockPrusaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var queueStats = new SlicerQueueStats();
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(It.IsAny<SlicerEngineType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueStats);
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueStats);

        _mockFileStorage.Setup(f => f.FileExistsAsync("health-check-non-existent-file", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        var result = await _orchestrator.GetHealthAsync();

        // Assert
        result.IsHealthy.Should().BeFalse();
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
                Quality = "standard"
            },
            Priority = SlicingJobPriority.Normal,
            Metadata = new Dictionary<string, object>()
        };
    }

    private static DistributedSlicingJob CreateDistributedSlicingJob(Guid? jobId = null)
    {
        return new DistributedSlicingJob
        {
            Id = jobId ?? Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "https://storage.example.com/models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
            Profile = new SlicerProfileDto(),
            Priority = SlicingJobPriority.Normal,
            Status = SlicingJobStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };
    }
}