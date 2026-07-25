using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services.Slicing;

public class SliceJobEventServiceTests
{
    private readonly SliceJobEventService _service;
    private readonly Mock<IHubContext<SlicerProgressHub>> _hubContextMock;
    private readonly Mock<ILogger<SliceJobEventService>> _loggerMock;
    private readonly Mock<IClientProxy> _clientProxyMock;
    private readonly Mock<IHubClients> _hubClientsMock;

    public SliceJobEventServiceTests()
    {
        _hubContextMock = new Mock<IHubContext<SlicerProgressHub>>();
        _loggerMock = new Mock<ILogger<SliceJobEventService>>();
        _clientProxyMock = new Mock<IClientProxy>();
        _hubClientsMock = new Mock<IHubClients>();

        // Setup hub context clients
        _hubContextMock.Setup(h => h.Clients).Returns(_hubClientsMock.Object);

        // Setup clients to return a client proxy for scoped group calls.
        _hubClientsMock.Setup(c => c.Group(It.IsAny<string>())).Returns(_clientProxyMock.Object);

        _service = new SliceJobEventService(_hubContextMock.Object, _loggerMock.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullHubContext_ThrowsArgumentNullException()
    {
        // Act & Assert
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() =>
            new SliceJobEventService(null!, _loggerMock.Object));
        Assert.Equal("hubContext", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() =>
            new SliceJobEventService(_hubContextMock.Object, null!));
        Assert.Equal("logger", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithValidDependencies_Succeeds()
    {
        // Act & Assert
        var service = new SliceJobEventService(_hubContextMock.Object, _loggerMock.Object);
        Assert.NotNull(service);
    }

    #endregion

    #region NotifyJobQueuedAsync Tests

    [Fact]
    public async Task NotifyJobQueuedAsync_WithNullJob_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.NotifyJobQueuedAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task NotifyJobQueuedAsync_WithValidJob_BroadcastsAndLogs()
    {
        // Arrange
        SliceJob job = CreateSliceJob();

        // Act
        await _service.NotifyJobQueuedAsync(job, CancellationToken.None);

        VerifyScopedBroadcasts(job);
    }

    #endregion

    #region NotifyJobStartedAsync Tests

    [Fact]
    public async Task NotifyJobStartedAsync_WithNullJob_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.NotifyJobStartedAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task NotifyJobStartedAsync_WithValidJob_BroadcastsAndLogs()
    {
        // Arrange
        SliceJob job = CreateSliceJob();

        // Act
        await _service.NotifyJobStartedAsync(job, CancellationToken.None);

        VerifyScopedBroadcasts(job);
    }

    #endregion

    #region NotifyJobProgressAsync Tests

    [Fact]
    public async Task NotifyJobProgressAsync_WithNullJob_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.NotifyJobProgressAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task NotifyJobProgressAsync_WithValidJob_BroadcastsAndLogs()
    {
        // Arrange
        SliceJob job = CreateSliceJob();

        // Act
        await _service.NotifyJobProgressAsync(job, CancellationToken.None);

        VerifyScopedBroadcasts(job);
    }

    #endregion

    #region NotifyJobCompletedAsync Tests

    [Fact]
    public async Task NotifyJobCompletedAsync_WithNullJob_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.NotifyJobCompletedAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task NotifyJobCompletedAsync_WithValidJob_BroadcastsAndLogs()
    {
        // Arrange
        SliceJob job = CreateSliceJob();

        // Act
        await _service.NotifyJobCompletedAsync(job, CancellationToken.None);

        VerifyScopedBroadcasts(job);
        _clientProxyMock.Verify(c => c.SendCoreAsync(
            "slicejobevent",
            It.Is<object[]>(args =>
                args.Length == 1 &&
                ((SliceJobEvent)args[0]).ArtifactsRoute == $"/api/artifacts/job/{job.Id}"),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    #endregion

    #region NotifyJobFailedAsync Tests

    [Fact]
    public async Task NotifyJobFailedAsync_WithNullJob_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.NotifyJobFailedAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task NotifyJobFailedAsync_WithValidJob_BroadcastsAndLogs()
    {
        // Arrange
        SliceJob job = CreateSliceJob();
        job.ErrorMessage = @"Worker failed at D:\private\model.stl with token=secret";

        // Act
        await _service.NotifyJobFailedAsync(job, CancellationToken.None);

        VerifyScopedBroadcasts(job);
        _clientProxyMock.Verify(c => c.SendCoreAsync(
            "slicejobevent",
            It.Is<object[]>(args =>
                args.Length == 1 &&
                ((SliceJobEvent)args[0]).ErrorMessage == "Slicing failed."),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    #endregion

    #region NotifyJobCancelledAsync Tests

    [Fact]
    public async Task NotifyJobCancelledAsync_WithNullJob_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.NotifyJobCancelledAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task NotifyJobCancelledAsync_WithValidJob_BroadcastsAndLogs()
    {
        // Arrange
        SliceJob job = CreateSliceJob();

        // Act
        await _service.NotifyJobCancelledAsync(job, CancellationToken.None);

        VerifyScopedBroadcasts(job);
    }

    #endregion

    #region Helper Methods

    private static SliceJob CreateSliceJob()
    {
        return new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            ModelFileUrl = "https://example.com/model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = 1,
            Status = "Queued",
            Priority = 1,
            QueuedAt = DateTime.UtcNow,
            StartedAt = null,
            WorkerId = null,
        };
    }

    private void VerifyScopedBroadcasts(SliceJob job)
    {
        _hubClientsMock.Verify(
            c => c.Group(AuthorizedHubGroups.SliceJob(job.Id)),
            Times.Once);
        _hubClientsMock.Verify(
            c => c.Group(AuthorizedHubGroups.User(job.UserId)),
            Times.Once);
        _hubClientsMock.Verify(
            c => c.Group(AuthorizedHubGroups.SlicingMonitors),
            Times.Once);
        _hubClientsMock.Verify(c => c.All, Times.Never);
    }

    #endregion
}
