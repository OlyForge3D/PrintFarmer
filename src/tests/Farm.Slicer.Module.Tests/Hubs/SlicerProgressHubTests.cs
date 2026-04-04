using System.Security.Claims;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Hubs;

public class SlicerProgressHubTests
{
    private readonly Mock<ILogger<SlicerProgressHub>> _loggerMock;
    private readonly Mock<ISlicerProgressNotifier> _progressNotifierMock;
    private readonly Mock<ISliceJobRepository> _jobRepositoryMock;
    private readonly Mock<HubCallerContext> _contextMock;
    private readonly Mock<IGroupManager> _groupsMock;
    private readonly SlicerProgressHub _hub;
    private readonly Guid _currentUserId = Guid.NewGuid();

    public SlicerProgressHubTests()
    {
        _loggerMock = new Mock<ILogger<SlicerProgressHub>>();
        _progressNotifierMock = new Mock<ISlicerProgressNotifier>();
        _jobRepositoryMock = new Mock<ISliceJobRepository>();
        _contextMock = new Mock<HubCallerContext>();
        _groupsMock = new Mock<IGroupManager>();

        _contextMock.Setup(c => c.ConnectionId).Returns("test-connection-id");
        _contextMock.Setup(c => c.User).Returns(new ClaimsPrincipal(
        [
            new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _currentUserId.ToString()),
            ], "TestAuth"),
        ]));

        _hub = new SlicerProgressHub(
            _loggerMock.Object,
            _progressNotifierMock.Object,
            _jobRepositoryMock.Object)
        {
            Context = _contextMock.Object,
            Groups = _groupsMock.Object,
        };
    }

    [Fact]
    public async Task JoinUserGroupAsync_WhenUserMatches_AddsConnectionToGroup()
    {
        await _hub.JoinUserGroupAsync(_currentUserId);

        _groupsMock.Verify(
            g => g.AddToGroupAsync("test-connection-id", $"User-{_currentUserId}", default),
            Times.Once);
    }

    [Fact]
    public async Task JoinUserGroupAsync_WhenUserDoesNotMatch_ThrowsHubException()
    {
        await Assert.ThrowsAsync<HubException>(() => _hub.JoinUserGroupAsync(Guid.NewGuid()));

        _groupsMock.Verify(
            g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SubscribeToJobAsync_WhenUserOwnsJob_SubscribesConnection()
    {
        Guid jobId = Guid.NewGuid();
        _jobRepositoryMock
            .Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SliceJob { Id = jobId, UserId = _currentUserId });

        await _hub.SubscribeToJobAsync(jobId);

        _progressNotifierMock.Verify(
            n => n.SubscribeToJobAsync(jobId, "test-connection-id", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubscribeToJobAsync_WhenUserDoesNotOwnJob_ThrowsHubException()
    {
        Guid jobId = Guid.NewGuid();
        _jobRepositoryMock
            .Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SliceJob { Id = jobId, UserId = Guid.NewGuid() });

        await Assert.ThrowsAsync<HubException>(() => _hub.SubscribeToJobAsync(jobId));

        _progressNotifierMock.Verify(
            n => n.SubscribeToJobAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
