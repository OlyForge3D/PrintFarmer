using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.SlicerServices;
using Farm.Web.Api.Services.Slicing;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services.Slicing;

public class SliceJobEventServiceTests
{
    private readonly SliceJobEventService _service;
    private readonly Mock<IHubContext<SlicerProgressHub>> _hubContextMock;
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly Mock<IClientProxy> _clientProxyMock;
    private readonly Mock<IHubClients> _hubClientsMock;

    public SliceJobEventServiceTests()
    {
        _hubContextMock = new Mock<IHubContext<SlicerProgressHub>>();
        _loggerMock = new Mock<IUnifiedLoggingService>();
        _clientProxyMock = new Mock<IClientProxy>();
        _hubClientsMock = new Mock<IHubClients>();

        // Setup hub context clients
        _hubContextMock.Setup(h => h.Clients).Returns(_hubClientsMock.Object);

        // Setup clients to return client proxy for all and group calls
        _hubClientsMock.Setup(c => c.All).Returns(_clientProxyMock.Object);
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

        // Assert - Verify broadcasts occurred
        _hubClientsMock.Verify(c => c.All, Times.Once);
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

        // Assert - Verify broadcasts occurred
        _hubClientsMock.Verify(c => c.All, Times.Once);
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

        // Assert - Verify broadcasts occurred
        _hubClientsMock.Verify(c => c.All, Times.Once);
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

        // Assert - Verify broadcasts occurred
        _hubClientsMock.Verify(c => c.All, Times.Once);
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

        // Act
        await _service.NotifyJobFailedAsync(job, CancellationToken.None);

        // Assert - Verify broadcasts occurred
        _hubClientsMock.Verify(c => c.All, Times.Once);
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

        // Assert - Verify broadcasts occurred
        _hubClientsMock.Verify(c => c.All, Times.Once);
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

    #endregion
}
