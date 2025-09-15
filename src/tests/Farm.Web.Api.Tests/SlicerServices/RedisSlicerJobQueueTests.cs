using System.Text.Json;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Shared;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace Farm.Web.Api.Tests.SlicerServices;

/// <summary>
/// Unit tests for RedisSlicerJobQueue - Redis-based distributed job queue
/// Note: These tests use mocked Redis interfaces to avoid requiring a real Redis instance
/// </summary>
[Trait("Category", "DbHeavy")]
public class RedisSlicerJobQueueTests
{
    private readonly Mock<IConnectionMultiplexer> _mockRedis;
    private readonly Mock<IDatabase> _mockDatabase;
    private readonly Mock<ILogger<RedisSlicerJobQueue>> _mockLogger;
    private readonly RedisSlicerJobQueue _queue;

    public RedisSlicerJobQueueTests()
    {
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();
        _mockLogger = new Mock<ILogger<RedisSlicerJobQueue>>();

        _mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDatabase.Object);

        _queue = new RedisSlicerJobQueue(_mockRedis.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task EnqueueAsync_ValidJob_ShouldStoreJobAndAddToQueue()
    {
        // Arrange
        var job = CreateDistributedSlicingJob();

        var transactionMock = new Mock<ITransaction>();
        transactionMock.Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>())).ReturnsAsync(true);
        _mockDatabase.Setup(d => d.CreateTransaction(It.IsAny<object>())).Returns(transactionMock.Object);

        // Act
        await _queue.EnqueueAsync(job);

        // Assert
        transactionMock.Verify(t => t.HashSetAsync(
                It.Is<RedisKey>(k => k.ToString().Contains($"slicer:job:{job.Id}")),
                It.IsAny<HashEntry[]>(),
                It.IsAny<CommandFlags>()
            ), Times.Once);

        transactionMock.Verify(t => t.SortedSetAddAsync(
                "slicer:queue",
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<CommandFlags>()
            ), Times.Once);

        transactionMock.Verify(t => t.ExecuteAsync(It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueAsync_HighPriorityJob_ShouldGetLowerScore()
    {
        // Arrange
        var normalJob = CreateDistributedSlicingJob();
        normalJob.Priority = SlicingJobPriority.Normal;

        var highPriorityJob = CreateDistributedSlicingJob();
        highPriorityJob.Priority = SlicingJobPriority.High;

        var capturedScores = new List<double>();

        var transactionMock = new Mock<ITransaction>();
        transactionMock.Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>())).ReturnsAsync(true);
        _mockDatabase.Setup(d => d.CreateTransaction(It.IsAny<object>())).Returns(transactionMock.Object);

        transactionMock.Setup(t => t.SortedSetAddAsync(
            "slicer:queue",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<CommandFlags>()
        )).Callback<RedisKey, RedisValue, double, CommandFlags>((k, v, score, f) => capturedScores.Add(score));

        // Act
        await _queue.EnqueueAsync(normalJob);
        await _queue.EnqueueAsync(highPriorityJob);

        // Assert
        capturedScores.Should().HaveCount(2);
        capturedScores[1].Should().BeLessThan(capturedScores[0], "High priority jobs should have lower scores");
    }

    [Fact]
    public async Task DequeueAsync_AvailableJob_ShouldReturnJobWithProcessingStatus()
    {
        // Arrange
        var job = CreateDistributedSlicingJob();
        var jobJson = JsonSerializer.Serialize(job);
        var workerId = "worker-123";

        _mockDatabase.Setup(d => d.SortedSetPopAsync("slicer:queue", Order.Ascending, CommandFlags.None))
            .ReturnsAsync(new SortedSetEntry(jobJson, 1000.0));

        // Act
        var result = await _queue.DequeueAsync(workerId);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(job.Id);
        result.Status.Should().Be(SlicingJobStatus.Slicing);
        result.WorkerId.Should().Be(workerId);
        result.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        _mockDatabase.Verify(d => d.SortedSetAddAsync(
            "slicer:processing",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<CommandFlags>()
        ), Times.Once);
    }

    [Fact]
    public async Task DequeueAsync_NoAvailableJobs_ShouldReturnNull()
    {
        // Arrange
        _mockDatabase.Setup(d => d.SortedSetPopAsync("slicer:queue", Order.Ascending, CommandFlags.None))
            .ReturnsAsync(new SortedSetEntry?());

        // Act
        var result = await _queue.DequeueAsync("worker-123");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task DequeueAsync_WithPreferredEngine_ShouldRequeueIncompatibleJob()
    {
        // Arrange
        var orcaJob = CreateDistributedSlicingJob();
        orcaJob.EngineType = SlicerEngineType.OrcaSlicer;

        var jobJson = JsonSerializer.Serialize(orcaJob);
        var workerId = "prusa-worker";

        _mockDatabase.Setup(d => d.SortedSetPopAsync("slicer:queue", Order.Ascending, CommandFlags.None))
            .ReturnsAsync(new SortedSetEntry(jobJson, 1000.0));

        // Act
        var result = await _queue.DequeueAsync(workerId, SlicerEngineType.PrusaSlicer);

        // Assert
        result.Should().BeNull();

        // Should requeue the job since it doesn't match the preferred engine
        _mockDatabase.Verify(d => d.SortedSetAddAsync(
            "slicer:queue",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<CommandFlags>()
        ), Times.Once);
    }

    [Fact]
    public async Task CompleteJobAsync_SuccessfulJob_ShouldMoveToCompletedQueue()
    {
        // Arrange
        var job = CreateDistributedSlicingJob();
        var result = new SlicingResult
        {
            Success = true,
            ResultFileUrl = new Uri("https://storage.example.com/result.gcode"),
            ProcessingTimeSeconds = 120.5,
            EstimatedPrintTimeSeconds = 3600,
            EstimatedFilamentUsageGrams = 25.0,
            LayerCount = 250,
            OutputFileSizeBytes = 1024 * 1024
        };

        // Act
        await _queue.CompleteJobAsync(job, result);

        // Assert
        job.Status.Should().Be(SlicingJobStatus.Completed);
        job.CompletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        job.ResultFileUrl.Should().Be(result.ResultFileUrl);
        job.EstimatedPrintTimeSeconds.Should().Be(result.EstimatedPrintTimeSeconds);
        job.EstimatedFilamentUsageGrams.Should().Be(result.EstimatedFilamentUsageGrams);
        job.LayerCount.Should().Be(result.LayerCount);

        _mockDatabase.Verify(d => d.SortedSetRemoveAsync(
            "slicer:processing",
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()
        ), Times.Once);

        _mockDatabase.Verify(d => d.SortedSetAddAsync(
            "slicer:completed",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<CommandFlags>()
        ), Times.Once);
    }

    [Fact]
    public async Task CompleteJobAsync_FailedJob_ShouldMoveToFailedQueue()
    {
        // Arrange
        var job = CreateDistributedSlicingJob();
        var result = new SlicingResult
        {
            Success = false,
            Error = "Model validation failed",
            ProcessingTimeSeconds = 30.0
        };

        // Act
        await _queue.CompleteJobAsync(job, result);

        // Assert
        job.Status.Should().Be(SlicingJobStatus.Error);
        job.CompletedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        job.ErrorMessage.Should().Be(result.Error);

        _mockDatabase.Verify(d => d.SortedSetAddAsync(
            "slicer:failed",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<CommandFlags>()
        ), Times.Once);
    }

    [Fact]
    public async Task FailJobAsync_ExistingJob_ShouldMarkJobAsFailed()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var job = CreateDistributedSlicingJob(jobId);
        var errorMessage = "Processing timeout occurred";

        var jobKey = $"slicer:job:{jobId}";
        var jobJson = JsonSerializer.Serialize(job);

        _mockDatabase.Setup(d => d.HashGetAsync(jobKey, "data", CommandFlags.None))
            .ReturnsAsync(jobJson);

        // Act
        await _queue.FailJobAsync(jobId, errorMessage);

        // Assert
        _mockDatabase.Verify(d => d.SortedSetAddAsync(
            "slicer:failed",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<CommandFlags>()
        ), Times.Once);
    }

    [Fact]
    public async Task UpdateProgressAsync_ValidJobId_ShouldUpdateJobProgress()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var job = CreateDistributedSlicingJob(jobId);
        var progress = 75;
        var currentStep = "Processing layer 150/200";

        var jobKey = $"slicer:job:{jobId}";
        var jobJson = JsonSerializer.Serialize(job);

        _mockDatabase.Setup(d => d.HashGetAsync(jobKey, "data", CommandFlags.None))
            .ReturnsAsync(jobJson);

        // Act
        await _queue.UpdateProgressAsync(jobId, progress, currentStep);

        // Assert
        _mockDatabase.Verify(d => d.HashSetAsync(
            jobKey,
            It.Is<HashEntry[]>(entries =>
                entries.Any(e => e.Name == "progress" && e.Value == progress) &&
                entries.Any(e => e.Name == "current_step" && e.Value == currentStep)
            ),
            It.IsAny<CommandFlags>()
        ), Times.Once);
    }

    [Fact]
    public async Task GetJobAsync_ExistingJob_ShouldReturnJob()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var job = CreateDistributedSlicingJob(jobId);
        var jobJson = JsonSerializer.Serialize(job);
        var jobKey = $"slicer:job:{jobId}";

        _mockDatabase.Setup(d => d.HashGetAsync(jobKey, "data", CommandFlags.None))
            .ReturnsAsync(jobJson);

        // Act
        var result = await _queue.GetJobAsync(jobId);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(jobId);
        result.UserId.Should().Be(job.UserId);
        result.SlicerEngine.Should().Be(job.SlicerEngine);
    }

    [Fact]
    public async Task GetJobAsync_NonExistentJob_ShouldReturnNull()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var jobKey = $"slicer:job:{jobId}";

        _mockDatabase.Setup(d => d.HashGetAsync(jobKey, "data", CommandFlags.None))
            .ReturnsAsync(RedisValue.Null);

        // Act
        var result = await _queue.GetJobAsync(jobId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task CancelJobAsync_QueuedJob_ShouldMoveToCompletedWithCancelledStatus()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var job = CreateDistributedSlicingJob(jobId);
        job.Status = SlicingJobStatus.Queued;

        var jobKey = $"slicer:job:{jobId}";
        var jobJson = JsonSerializer.Serialize(job);

        _mockDatabase.Setup(d => d.HashGetAsync(jobKey, "data", It.IsAny<CommandFlags>()))
            .ReturnsAsync(jobJson);

        // Act
        await _queue.CancelJobAsync(jobId);

        // Assert
        _mockDatabase.Verify(d => d.SortedSetRemoveAsync(
            "slicer:queue",
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()
        ), Times.Once);

        _mockDatabase.Verify(d => d.SortedSetAddAsync(
            "slicer:completed",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<CommandFlags>()
        ), Times.Once);
    }

    [Fact]
    public async Task CancelJobAsync_ProcessingJob_ShouldMoveFromProcessingToCompleted()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var job = CreateDistributedSlicingJob(jobId);
        job.Status = SlicingJobStatus.Slicing;

        var jobKey = $"slicer:job:{jobId}";
        var jobJson = JsonSerializer.Serialize(job);

        _mockDatabase.Setup(d => d.HashGetAsync(jobKey, "data", It.IsAny<CommandFlags>()))
            .ReturnsAsync(jobJson);

        // Act
        await _queue.CancelJobAsync(jobId);

        // Assert
        _mockDatabase.Verify(d => d.SortedSetRemoveAsync(
            "slicer:processing",
            It.IsAny<RedisValue>(),
            It.IsAny<CommandFlags>()
        ), Times.Once);
    }

    [Fact]
    public async Task GetQueueStatsAsync_ShouldReturnQueueStatistics()
    {
        // Arrange
        var engine = SlicerEngineType.OrcaSlicer;

        _mockDatabase.Setup(d => d.SortedSetLengthAsync("slicer:queue", It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Exclude>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(5);
        _mockDatabase.Setup(d => d.SortedSetLengthAsync("slicer:processing", It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Exclude>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(2);
        _mockDatabase.Setup(d => d.SortedSetLengthAsync("slicer:completed", It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Exclude>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(100);
        _mockDatabase.Setup(d => d.SortedSetLengthAsync("slicer:failed", It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Exclude>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(3);

        // Act
        var result = await _queue.GetQueueStatsAsync(engine);

        // Assert
        result.Should().NotBeNull();
        result.Engine.Should().Be(engine);
        result.QueuedJobs.Should().Be(5);
        result.ProcessingJobs.Should().Be(2);
        result.CompletedJobs.Should().Be(100);
        result.FailedJobs.Should().Be(3);
        result.EstimatedWaitTime.Should().BeGreaterThan(TimeSpan.Zero);
        result.LastUpdated.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetUserJobsAsync_ShouldReturnUserSpecificJobs()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var userJob1 = CreateDistributedSlicingJob();
        userJob1.UserId = userId;

        var userJob2 = CreateDistributedSlicingJob();
        userJob2.UserId = userId;

        var otherUserJob = CreateDistributedSlicingJob();
        otherUserJob.UserId = Guid.NewGuid();

        var completedJobs = new RedisValue[]
        {
            JsonSerializer.Serialize(userJob1),
            JsonSerializer.Serialize(otherUserJob),
            JsonSerializer.Serialize(userJob2)
        };

        _mockDatabase.Setup(d => d.SortedSetRangeByRankAsync("slicer:completed", 0, 100, Order.Descending, It.IsAny<CommandFlags>()))
            .ReturnsAsync(completedJobs);
        _mockDatabase.Setup(d => d.SortedSetRangeByRankAsync("slicer:failed", 0, 100, Order.Descending, It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);

        // Act
        var result = await _queue.GetUserJobsAsync(userId, 10);

        // Assert
        result.Should().HaveCount(2);
        result.All(j => j.UserId == userId).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupOldJobsAsync_ShouldRemoveOldJobsFromQueues()
    {
        // Arrange
        var maxAge = TimeSpan.FromDays(7);
        var cutoffTimestamp = DateTimeOffset.UtcNow.Subtract(maxAge).ToUnixTimeSeconds();

        _mockDatabase.Setup(d => d.SortedSetRemoveRangeByScoreAsync("slicer:completed", 0, cutoffTimestamp, It.IsAny<Exclude>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(15);
        _mockDatabase.Setup(d => d.SortedSetRemoveRangeByScoreAsync("slicer:failed", 0, cutoffTimestamp, It.IsAny<Exclude>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(3);

        // Act
        await _queue.CleanupOldJobsAsync(maxAge);

        // Assert
        _mockDatabase.Verify(d => d.SortedSetRemoveRangeByScoreAsync("slicer:completed", 0, cutoffTimestamp, It.IsAny<Exclude>(), It.IsAny<CommandFlags>()), Times.Once);
        _mockDatabase.Verify(d => d.SortedSetRemoveRangeByScoreAsync("slicer:failed", 0, cutoffTimestamp, It.IsAny<Exclude>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task RequeueFailedJobsAsync_ShouldRequeueEligibleFailedJobs()
    {
        // Arrange
        var maxRetryCount = 3;
        var retryableJob = CreateDistributedSlicingJob();
        retryableJob.RetryCount = 1;

        var maxRetriesJob = CreateDistributedSlicingJob();
        maxRetriesJob.RetryCount = 3;

        var failedJobs = new RedisValue[]
        {
            JsonSerializer.Serialize(retryableJob),
            JsonSerializer.Serialize(maxRetriesJob)
        };

        _mockDatabase.Setup(d => d.SortedSetRangeByRankAsync("slicer:failed", 0, 100, It.IsAny<Order>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(failedJobs);

        var transactionMock = new Mock<ITransaction>();
        transactionMock.Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>())).ReturnsAsync(true);
        _mockDatabase.Setup(d => d.CreateTransaction(It.IsAny<object>())).Returns(transactionMock.Object);

        // Act
        await _queue.RequeueFailedJobsAsync(maxRetryCount);

        // Assert
        // Should remove the retryable job from failed queue
        _mockDatabase.Verify(d => d.SortedSetRemoveAsync("slicer:failed", It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Once);

        // Should enqueue it again
        transactionMock.Verify(t => t.SortedSetAddAsync("slicer:queue", It.IsAny<RedisValue>(), It.IsAny<double>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [Theory]
    [InlineData(SlicingJobPriority.Critical, SlicingJobPriority.High)]
    [InlineData(SlicingJobPriority.High, SlicingJobPriority.Normal)]
    [InlineData(SlicingJobPriority.Normal, SlicingJobPriority.Low)]
    public async Task EnqueueAsync_PriorityOrdering_ShouldAssignCorrectScores(SlicingJobPriority higherPriority, SlicingJobPriority lowerPriority)
    {
        // Arrange
        var highPriorityJob = CreateDistributedSlicingJob();
        highPriorityJob.Priority = higherPriority;

        var lowPriorityJob = CreateDistributedSlicingJob();
        lowPriorityJob.Priority = lowerPriority;

        var capturedScores = new List<double>();

        var transactionMock = new Mock<ITransaction>();
        transactionMock.Setup(t => t.ExecuteAsync(It.IsAny<CommandFlags>())).ReturnsAsync(true);
        _mockDatabase.Setup(d => d.CreateTransaction(It.IsAny<object>())).Returns(transactionMock.Object);

        transactionMock.Setup(t => t.SortedSetAddAsync(
            "slicer:queue",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<CommandFlags>()
            )).Callback<RedisKey, RedisValue, double, CommandFlags>((k, v, score, f) => capturedScores.Add(score));

        // Act
        await _queue.EnqueueAsync(highPriorityJob);
        await _queue.EnqueueAsync(lowPriorityJob);

        // Assert
        capturedScores.Should().HaveCount(2);
        capturedScores[0].Should().BeLessThan(capturedScores[1], "Higher priority jobs should have lower scores for ascending order");
    }

    [Fact]
    public void Constructor_NullRedisConnection_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new RedisSlicerJobQueue(null!, _mockLogger.Object));
    }

    [Fact]
    public void Constructor_NullLogger_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new RedisSlicerJobQueue(_mockRedis.Object, null!));
    }

    // Helper methods

    private static DistributedSlicingJob CreateDistributedSlicingJob(Guid? jobId = null)
    {
        var job = new DistributedSlicingJob
        {
            Id = jobId ?? Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = new Uri("https://storage.example.com/models/test.stl"),
            ModelFileName = "test.stl",
            EngineType = SlicerEngineType.OrcaSlicer,
            Profile = new SlicerProfileDto
            {
                LayerHeight = 0.2,
                InfillPercentage = 20,
                Material = "PLA"
            },
            Priority = SlicingJobPriority.Normal,
            Status = SlicingJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            Progress = 0,
        };
        job.Metadata["key1"] = "value1";
        return job;
    }
}
