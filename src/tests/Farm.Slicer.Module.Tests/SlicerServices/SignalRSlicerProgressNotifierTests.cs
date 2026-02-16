using Farm.Infrastructure;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Slicer.Module.Tests.TestUtils;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace Farm.Slicer.Module.Tests.SlicerServices;

/// <summary>
/// Unit tests for SignalRSlicerProgressNotifier - SignalR-based progress notifications
/// </summary>
public class SignalRSlicerProgressNotifierTests
{
    private readonly Mock<IHubContext<SlicerProgressHub>> _mockHubContext;
    private readonly Mock<IHubClients> _mockClients;
    private readonly Mock<IClientProxy> _mockClientProxy;
    private readonly Mock<IGroupManager> _mockGroupManager;
    private readonly TestLoggingService _testLogger;
    private readonly SignalRSlicerProgressNotifier _notifier;

    public SignalRSlicerProgressNotifierTests()
    {
        _mockHubContext = new Mock<IHubContext<SlicerProgressHub>>();
        _mockClients = new Mock<IHubClients>();
        _mockClientProxy = new Mock<IClientProxy>();
        _mockGroupManager = new Mock<IGroupManager>();
        _testLogger = new TestLoggingService();

        // Setup hub context
        _ = _mockHubContext.Setup(h => h.Clients).Returns(_mockClients.Object);
        _ = _mockHubContext.Setup(h => h.Groups).Returns(_mockGroupManager.Object);

        // Setup clients to return mock proxies
        _ = _mockClients.Setup(c => c.Clients(It.IsAny<IReadOnlyList<string>>())).Returns(_mockClientProxy.Object);
        _ = _mockClients.Setup(c => c.Group(It.IsAny<string>())).Returns(_mockClientProxy.Object);

        _notifier = new SignalRSlicerProgressNotifier(_mockHubContext.Object, _testLogger);
    }

    [Fact]
    public async Task NotifyProgressAsync_WithValidUpdate_ShouldSendToMonitors()
    {
        // Arrange
        SlicingProgressUpdate update = new SlicingProgressUpdate
        {
            JobId = Guid.NewGuid(),
            Progress = 50,
            Status = SlicingJobStatus.Slicing,
            CurrentStep = "Processing layer 100/200",
            Timestamp = DateTime.UtcNow
        };

        // Act
        await _notifier.NotifyProgressAsync(update);

        // Assert
        _mockClients.Verify(c => c.Group("SlicingMonitors"), Times.Once);
        _mockClientProxy.Verify(p => p.SendCoreAsync(
            "slicingprogress",
            It.Is<object[]>(args => args.Length == 1 && args[0].Equals(update)),
            It.IsAny<CancellationToken>()
        ), Times.AtLeastOnce);
    }

    [Fact]
    public async Task NotifyProgressAsync_WithJobSubscribers_ShouldSendToSubscribers()
    {
        // Arrange
        Guid jobId = Guid.NewGuid();
        string connectionId = "connection-123";

        SlicingProgressUpdate update = new SlicingProgressUpdate
        {
            JobId = jobId,
            Progress = 75,
            Status = SlicingJobStatus.Slicing,
            CurrentStep = "Generating G-code",
            Timestamp = DateTime.UtcNow
        };

        // Subscribe to job first
        await _notifier.SubscribeToJobAsync(jobId, connectionId);

        // Act
        await _notifier.NotifyProgressAsync(update);

        // Assert
        _mockClients.Verify(c => c.Clients(It.Is<IReadOnlyList<string>>(list => list.Contains(connectionId))), Times.Once);
        _mockClientProxy.Verify(p => p.SendCoreAsync(
            "slicingprogress",
            It.Is<object[]>(args => args.Length == 1 && args[0].Equals(update)),
            It.IsAny<CancellationToken>()
        ), Times.AtLeastOnce);
    }

    [Fact]
    public async Task NotifyCompletionAsync_SuccessfulJob_ShouldSendCompletionNotification()
    {
        // Arrange
        DistributedSlicingJob job = CreateDistributedSlicingJob();
        SlicingResult result = new SlicingResult
        {
            Success = true,
            ResultFileUrl = new Uri("https://storage.example.com/results/result.gcode"),
            ProcessingTimeSeconds = 120.5,
            EstimatedPrintTimeSeconds = 3600,
            EstimatedFilamentUsageGrams = 25.5,
            LayerCount = 250
        };

        job.CompletedAt = DateTime.UtcNow;
        job.Status = SlicingJobStatus.Completed;

        // Act
        await _notifier.NotifyCompletionAsync(job, result);

        // Assert
        _mockClients.Verify(c => c.Group($"User-{job.UserId}"), Times.Once);
        _mockClients.Verify(c => c.Group("SlicingMonitors"), Times.Once);

        _mockClientProxy.Verify(p => p.SendCoreAsync(
            "slicingcompleted",
            It.Is<object[]>(args => args.Length == 1 &&
                ((SlicingCompletionNotification)args[0]).JobId == job.Id &&
                ((SlicingCompletionNotification)args[0]).Success &&
                ((SlicingCompletionNotification)args[0]).ResultFileUrl != null &&
                ((SlicingCompletionNotification)args[0]).ResultFileUrl!.ToString() == result.ResultFileUrl!.ToString()
            ),
            It.IsAny<CancellationToken>()
        ), Times.AtLeast(2)); // Once for user group, once for monitors
    }

    [Fact]
    public async Task NotifyCompletionAsync_FailedJob_ShouldSendFailureNotification()
    {
        // Arrange
        DistributedSlicingJob job = CreateDistributedSlicingJob();
        SlicingResult result = new SlicingResult
        {
            Success = false,
            Error = "Slicing failed due to invalid model",
            ProcessingTimeSeconds = 45.2
        };

        job.CompletedAt = DateTime.UtcNow;
        job.Status = SlicingJobStatus.Error;

        // Act
        await _notifier.NotifyCompletionAsync(job, result);

        // Assert
        _mockClients.Verify(c => c.Group($"User-{job.UserId}"), Times.Once);
        _mockClients.Verify(c => c.Group("SlicingMonitors"), Times.Once);

        _mockClientProxy.Verify(p => p.SendCoreAsync(
            "slicingcompleted",
            It.Is<object[]>(args => args.Length == 1 &&
                ((SlicingCompletionNotification)args[0]).JobId == job.Id &&
                !((SlicingCompletionNotification)args[0]).Success &&
                ((SlicingCompletionNotification)args[0]).ErrorMessage == result.Error
            ),
            It.IsAny<CancellationToken>()
        ), Times.AtLeast(2));
    }

    [Fact]
    public async Task NotifyCompletionAsync_WithJobSubscribers_ShouldSendToSubscribersAndCleanup()
    {
        // Arrange
        DistributedSlicingJob job = CreateDistributedSlicingJob();
        string connectionId1 = "connection-1";
        string connectionId2 = "connection-2";

        SlicingResult result = new SlicingResult { Success = true };

        // Subscribe connections to job
        await _notifier.SubscribeToJobAsync(job.Id, connectionId1);
        await _notifier.SubscribeToJobAsync(job.Id, connectionId2);

        // Act
        await _notifier.NotifyCompletionAsync(job, result);

        // Assert
        _mockClients.Verify(c => c.Clients(It.Is<IReadOnlyList<string>>(list =>
            list.Contains(connectionId1) && list.Contains(connectionId2)
        )), Times.Once);

        // Verify subsequent progress updates don't go to these connections (cleanup worked)
        SlicingProgressUpdate progressUpdate = new SlicingProgressUpdate { JobId = job.Id, Progress = 100 };
        await _notifier.NotifyProgressAsync(progressUpdate);

        // Should only send to monitors, not to the cleaned-up subscribers
        _mockClients.Verify(c => c.Group("SlicingMonitors"), Times.AtLeast(1));
    }

    [Fact]
    public async Task NotifyFailureAsync_ShouldSendFailureNotification()
    {
        // Arrange
        DistributedSlicingJob job = CreateDistributedSlicingJob();
        string errorMessage = "Job cancelled by user";

        // Act
        await _notifier.NotifyFailureAsync(job, errorMessage);

        // Assert
        _mockClients.Verify(c => c.Group($"User-{job.UserId}"), Times.Once);
        _mockClients.Verify(c => c.Group("SlicingMonitors"), Times.Once);

        _mockClientProxy.Verify(p => p.SendCoreAsync(
            "slicingfailed",
            It.Is<object[]>(args => args.Length == 1 &&
                ((SlicingFailureNotification)args[0]).JobId == job.Id &&
                ((SlicingFailureNotification)args[0]).ErrorMessage == errorMessage
            ),
            It.IsAny<CancellationToken>()
        ), Times.AtLeast(2));
    }

    [Fact]
    public async Task SubscribeToJobAsync_ShouldAddConnectionToJobSubscriptions()
    {
        // Arrange
        Guid jobId = Guid.NewGuid();
        string connectionId = "connection-123";

        // Act
        await _notifier.SubscribeToJobAsync(jobId, connectionId);

        // Verify by sending a progress update and checking if it goes to the subscriber
        SlicingProgressUpdate update = new SlicingProgressUpdate { JobId = jobId, Progress = 25 };
        await _notifier.NotifyProgressAsync(update);

        // Assert
        _mockClients.Verify(c => c.Clients(It.Is<IReadOnlyList<string>>(list => list.Contains(connectionId))), Times.Once);
    }

    [Fact]
    public async Task SubscribeToJobAsync_MultipleConnections_ShouldTrackAllSubscriptions()
    {
        // Arrange
        Guid jobId = Guid.NewGuid();
        string connectionId1 = "connection-1";
        string connectionId2 = "connection-2";
        string connectionId3 = "connection-3";

        // Act
        await _notifier.SubscribeToJobAsync(jobId, connectionId1);
        await _notifier.SubscribeToJobAsync(jobId, connectionId2);
        await _notifier.SubscribeToJobAsync(jobId, connectionId3);

        // Send progress update
        SlicingProgressUpdate update = new SlicingProgressUpdate { JobId = jobId, Progress = 50 };
        await _notifier.NotifyProgressAsync(update);

        // Assert
        _mockClients.Verify(c => c.Clients(It.Is<IReadOnlyList<string>>(list =>
            list.Contains(connectionId1) &&
            list.Contains(connectionId2) &&
            list.Contains(connectionId3)
        )), Times.Once);
    }

    [Fact]
    public async Task UnsubscribeFromJobAsync_ShouldRemoveConnectionFromSubscriptions()
    {
        // Arrange
        Guid jobId = Guid.NewGuid();
        string connectionId1 = "connection-1";
        string connectionId2 = "connection-2";

        // Subscribe both connections
        await _notifier.SubscribeToJobAsync(jobId, connectionId1);
        await _notifier.SubscribeToJobAsync(jobId, connectionId2);

        // Act - Unsubscribe one connection
        await _notifier.UnsubscribeFromJobAsync(jobId, connectionId1);

        // Send progress update
        SlicingProgressUpdate update = new SlicingProgressUpdate { JobId = jobId, Progress = 75 };
        await _notifier.NotifyProgressAsync(update);

        // Assert - Should only send to remaining subscriber
        _mockClients.Verify(c => c.Clients(It.Is<IReadOnlyList<string>>(list =>
            list.Contains(connectionId2) && !list.Contains(connectionId1)
        )), Times.Once);
    }

    [Fact]
    public async Task UnsubscribeFromJobAsync_LastSubscriber_ShouldRemoveJobFromTracking()
    {
        // Arrange
        Guid jobId = Guid.NewGuid();
        string connectionId = "connection-only";

        // Subscribe and then unsubscribe
        await _notifier.SubscribeToJobAsync(jobId, connectionId);
        await _notifier.UnsubscribeFromJobAsync(jobId, connectionId);

        // Act - Send progress update
        SlicingProgressUpdate update = new SlicingProgressUpdate { JobId = jobId, Progress = 100 };
        await _notifier.NotifyProgressAsync(update);

        // Assert - Should only send to monitors, not to any specific clients
        _mockClients.Verify(c => c.Clients(It.IsAny<IReadOnlyList<string>>()), Times.Never);
        _mockClients.Verify(c => c.Group("SlicingMonitors"), Times.Once);
    }

    [Fact]
    public async Task UnsubscribeFromJobAsync_NonExistentJob_ShouldNotThrow()
    {
        // Arrange
        Guid jobId = Guid.NewGuid();
        string connectionId = "connection-123";

        // Act & Assert - Should not throw
        await _notifier.UnsubscribeFromJobAsync(jobId, connectionId);
    }

    [Fact]
    public async Task UnsubscribeFromJobAsync_NonExistentConnection_ShouldNotThrow()
    {
        // Arrange
        Guid jobId = Guid.NewGuid();
        string connectionId1 = "connection-1";
        string connectionId2 = "connection-2";

        // Subscribe one connection
        await _notifier.SubscribeToJobAsync(jobId, connectionId1);

        // Act & Assert - Unsubscribing non-existent connection should not throw
        await _notifier.UnsubscribeFromJobAsync(jobId, connectionId2);
    }

    [Fact]
    public async Task ConcurrentOperations_ShouldHandleThreadSafety()
    {
        // Arrange
        Guid jobId = Guid.NewGuid();
        List<string> connectionIds = Enumerable.Range(1, 10).Select(i => $"connection-{i}").ToList();
        List<Task> tasks = new List<Task>();

        // Act - Concurrent subscribes and unsubscribes
        foreach (string? connectionId in connectionIds.Take(5))
        {
            tasks.Add(_notifier.SubscribeToJobAsync(jobId, connectionId));
        }

        foreach (string? connectionId in connectionIds.Skip(2).Take(3))
        {
            tasks.Add(_notifier.UnsubscribeFromJobAsync(jobId, connectionId));
        }

        // Add some progress notifications
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(_notifier.NotifyProgressAsync(new SlicingProgressUpdate
            {
                JobId = jobId,
                Progress = i * 20
            }));
        }

        // Wait for all operations to complete
        await Task.WhenAll(tasks);

        // Assert - No exceptions should be thrown
        _ = tasks.All(t => t.IsCompletedSuccessfully).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, "Initializing")]
    [InlineData(25, "Processing layer 50/200")]
    [InlineData(50, "Slicing in progress")]
    [InlineData(75, "Generating G-code")]
    [InlineData(100, "Finalizing output")]
    public async Task NotifyProgressAsync_DifferentProgressValues_ShouldSendCorrectUpdates(int progress, string step)
    {
        // Arrange
        SlicingProgressUpdate update = new SlicingProgressUpdate
        {
            JobId = Guid.NewGuid(),
            Progress = progress,
            Status = SlicingJobStatus.Slicing,
            CurrentStep = step,
            Timestamp = DateTime.UtcNow
        };

        // Act
        await _notifier.NotifyProgressAsync(update);

        // Assert
        _mockClientProxy.Verify(p => p.SendCoreAsync(
            "slicingprogress",
            It.Is<object[]>(args => args.Length == 1 &&
                ((SlicingProgressUpdate)args[0]).Progress == progress &&
                ((SlicingProgressUpdate)args[0]).CurrentStep == step
            ),
            It.IsAny<CancellationToken>()
        ), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Constructor_NullHubContext_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        _ = Assert.Throws<ArgumentNullException>(() =>
            new SignalRSlicerProgressNotifier(null!, _testLogger));
    }

    [Fact]
    public async Task Constructor_NullLogger_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        _ = Assert.Throws<ArgumentNullException>(() =>
            new SignalRSlicerProgressNotifier(_mockHubContext.Object, null!));
    }

    // Helper methods

    private static DistributedSlicingJob CreateDistributedSlicingJob()
    {
        return new DistributedSlicingJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = new Uri("https://storage.example.com/models/test.stl"),
            ModelFileName = "test.stl",
            EngineType = SlicerEngineType.OrcaSlicer,
            Profile = new SlicerProfileDto(),
            Priority = SlicingJobPriority.Normal,
            Status = SlicingJobStatus.Slicing,
            CreatedAt = DateTime.UtcNow,
            // Metadata is read-only; will remain empty for test
        };
    }
}

/// <summary>
/// Mock SignalR Hub for testing
/// </summary>
// NOTE: Removed duplicate SlicerProgressHub test stub; using production hub class instead.
