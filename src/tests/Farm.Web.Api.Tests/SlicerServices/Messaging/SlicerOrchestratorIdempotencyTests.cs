using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using Farm.Web.Shared.Slicer.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.SlicerServices.Messaging;

/// <summary>
/// Integration tests for SlicerOrchestrator idempotency functionality
/// </summary>
public class SlicerOrchestratorIdempotencyTests
{
    private readonly Mock<ISlicerJobQueue> _mockJobQueue;
    private readonly Mock<ISlicerFileStorage> _mockFileStorage;
    private readonly Mock<ISlicerProgressNotifier> _mockProgressNotifier;
    private readonly Mock<ISlicerEngine> _mockOrcaSlicerEngine;
    private readonly Mock<ILogger<SlicerOrchestrator>> _mockLogger;
    private readonly SlicerOrchestrator _orchestrator;

    public SlicerOrchestratorIdempotencyTests()
    {
        _mockJobQueue = new Mock<ISlicerJobQueue>();
        _mockFileStorage = new Mock<ISlicerFileStorage>();
        _mockProgressNotifier = new Mock<ISlicerProgressNotifier>();
        _mockOrcaSlicerEngine = new Mock<ISlicerEngine>();
        _mockLogger = new Mock<ILogger<SlicerOrchestrator>>();

        // Setup mock engine
        _mockOrcaSlicerEngine.Setup(e => e.EngineType).Returns(SlicerEngineType.OrcaSlicer);
        _mockOrcaSlicerEngine.Setup(e => e.Version).Returns("1.8.0-test");
        _mockOrcaSlicerEngine.Setup(e => e.SupportedFileExtensions).Returns(new[] { ".stl", ".obj", ".3mf" });

        var engines = new[] { _mockOrcaSlicerEngine.Object };

        _orchestrator = new SlicerOrchestrator(
            _mockJobQueue.Object,
            _mockFileStorage.Object,
            _mockProgressNotifier.Object,
            engines,
            _mockLogger.Object);
    }

    [Fact]
    public async Task SubmitJobAsync_DuplicateSubmission_ShouldReturnExistingJob()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        
        // Create envelope with specific correlation ID to simulate duplicate
        var correlationId = Guid.NewGuid();
        var jobContent = SlicingJobContent.FromRequest(request);
        var envelope = MessageEnvelope.Create(jobContent, request.SlicerEngine, request.Priority, correlationId);
        request.Envelope = envelope;

        // Create existing job with same correlation and checksum
        var existingJob = new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            PrinterId = request.PrinterId,
            Status = SlicingJobStatus.Slicing,
            Progress = 50,
            CorrelationId = correlationId,
            Checksum = envelope.Checksum
        };

        var queueStats = new SlicerQueueStats
        {
            QueuedJobs = 3,
            EstimatedWaitTime = TimeSpan.FromMinutes(10)
        };

        // Setup mocks
        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.FileExistsAsync(request.ModelFileUrl, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.GetFileMetadataAsync(request.ModelFileUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerFileMetadata { SizeBytes = 1024 * 1024 });

        // Return existing job for duplicate check
        _mockJobQueue.Setup(q => q.FindExistingJobAsync(correlationId, envelope.Checksum, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingJob);
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(request.SlicerEngine, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueStats);

        // Act
        var result = await _orchestrator.SubmitJobAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.JobId.Should().Be(existingJob.Id); // Should return existing job ID
        result.Status.Should().Be(SlicingJobStatus.Slicing);

        // Verify that new job was NOT enqueued
        _mockJobQueue.Verify(q => q.EnqueueAsync(It.IsAny<DistributedSlicingJob>(), It.IsAny<CancellationToken>()), 
            Times.Never);
        
        // Verify that duplicate check was called
        _mockJobQueue.Verify(q => q.FindExistingJobAsync(correlationId, envelope.Checksum, It.IsAny<CancellationToken>()), 
            Times.Once);
    }

    [Fact]
    public async Task SubmitJobAsync_NewJob_ShouldCreateAndEnqueueJob()
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
            .ReturnsAsync(new SlicerFileMetadata { SizeBytes = 1024 * 1024 });

        // No existing job found
        _mockJobQueue.Setup(q => q.FindExistingJobAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DistributedSlicingJob?)null);
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(request.SlicerEngine, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueStats);

        // Act
        var result = await _orchestrator.SubmitJobAsync(request);

        // Assert
        result.Should().NotBeNull();
        result.JobId.Should().NotBeEmpty();
        result.Status.Should().Be(SlicingJobStatus.Queued);
        result.QueuePosition.Should().Be(5);

        // Verify that new job was enqueued
        _mockJobQueue.Verify(q => q.EnqueueAsync(It.Is<DistributedSlicingJob>(job =>
            job.UserId == request.UserId &&
            job.PrinterId == request.PrinterId &&
            job.ModelFileUrl == request.ModelFileUrl &&
            job.CorrelationId != Guid.Empty &&
            !string.IsNullOrEmpty(job.Checksum)
        ), It.IsAny<CancellationToken>()), Times.Once);

        // Verify duplicate check was performed
        _mockJobQueue.Verify(q => q.FindExistingJobAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), 
            Times.Once);
    }

    [Fact]
    public async Task SubmitJobAsync_ChecksumMismatch_ShouldThrowArgumentException()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        
        // Create envelope with different content than request (simulate tampering)
        var differentContent = new SlicingJobContent
        {
            UserId = Guid.NewGuid(), // Different user
            ModelFileUrl = "https://different.example.com/model.stl"
        };
        
        var envelope = MessageEnvelope.Create(differentContent, request.SlicerEngine, request.Priority);
        request.Envelope = envelope; // Attach envelope with mismatched checksum

        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.FileExistsAsync(request.ModelFileUrl, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // No existing job found
        _mockJobQueue.Setup(q => q.FindExistingJobAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DistributedSlicingJob?)null);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _orchestrator.SubmitJobAsync(request));
        exception.Message.Should().Contain("Request content does not match envelope checksum");
        exception.ParamName.Should().Be("request");

        // Verify that job was NOT enqueued due to checksum mismatch
        _mockJobQueue.Verify(q => q.EnqueueAsync(It.IsAny<DistributedSlicingJob>(), It.IsAny<CancellationToken>()), 
            Times.Never);
    }

    [Fact]
    public async Task SubmitJobAsync_AutoGeneratesEnvelope_WhenNoneProvided()
    {
        // Arrange
        var request = CreateValidSlicingJobRequest();
        // No envelope set - should auto-generate

        var queueStats = new SlicerQueueStats
        {
            QueuedJobs = 2,
            EstimatedWaitTime = TimeSpan.FromMinutes(8)
        };

        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.FileExistsAsync(request.ModelFileUrl, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.GetFileMetadataAsync(request.ModelFileUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerFileMetadata { SizeBytes = 1024 * 1024 });

        // No existing job found
        _mockJobQueue.Setup(q => q.FindExistingJobAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DistributedSlicingJob?)null);
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(request.SlicerEngine, It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueStats);

        // Act
        var result = await _orchestrator.SubmitJobAsync(request);

        // Assert
        result.Should().NotBeNull();

        // Verify that job was enqueued with auto-generated envelope data
        _mockJobQueue.Verify(q => q.EnqueueAsync(It.Is<DistributedSlicingJob>(job =>
            job.CorrelationId != Guid.Empty &&
            !string.IsNullOrEmpty(job.Checksum) &&
            job.Attempt == 1 &&
            job.EnvelopeVersion == MessageEnvelope.CurrentVersion
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitJobAsync_SameContentDifferentCorrelation_ShouldCreateSeparateJobs()
    {
        // Arrange
        var request1 = CreateValidSlicingJobRequest();
        var request2 = CreateValidSlicingJobRequest();
        
        // Same content but different correlation IDs
        request2.UserId = request1.UserId;
        request2.PrinterId = request1.PrinterId;
        request2.ModelFileUrl = request1.ModelFileUrl;
        
        var queueStats = new SlicerQueueStats
        {
            QueuedJobs = 1,
            EstimatedWaitTime = TimeSpan.FromMinutes(5)
        };

        _mockOrcaSlicerEngine.Setup(e => e.IsHealthyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.FileExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _mockFileStorage.Setup(f => f.GetFileMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlicerFileMetadata { SizeBytes = 1024 * 1024 });

        // No existing jobs found (different correlation IDs)
        _mockJobQueue.Setup(q => q.FindExistingJobAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DistributedSlicingJob?)null);
        _mockJobQueue.Setup(q => q.GetQueueStatsAsync(It.IsAny<SlicerEngineType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueStats);

        // Act
        var result1 = await _orchestrator.SubmitJobAsync(request1);
        var result2 = await _orchestrator.SubmitJobAsync(request2);

        // Assert
        result1.JobId.Should().NotBe(result2.JobId); // Different job IDs
        
        // Verify both jobs were enqueued
        _mockJobQueue.Verify(q => q.EnqueueAsync(It.IsAny<DistributedSlicingJob>(), It.IsAny<CancellationToken>()), 
            Times.Exactly(2));
    }

    // Helper method
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
}