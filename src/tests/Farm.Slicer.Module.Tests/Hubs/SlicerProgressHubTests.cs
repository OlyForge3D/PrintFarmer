using System.Security.Claims;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Authorization;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Slicer.Module.Tests.Hubs;

public sealed class SlicerProgressHubTests
{
    private readonly Mock<ILogger<SlicerProgressHub>> _logger = new();
    private readonly Mock<ISlicerProgressNotifier> _progressNotifier = new();
    private readonly Mock<ISliceJobRepository> _jobRepository = new();
    private readonly Mock<ISlicerResourceAccessAuthorizer> _resourceAccess = new();
    private readonly Mock<IGroupManager> _groups = new();
    private readonly Mock<HubCallerContext> _context = new();
    private readonly SlicerProgressHub _hub;

    public SlicerProgressHubTests()
    {
        _context.Setup(context => context.ConnectionId).Returns("connection-1");
        _context.Setup(context => context.ConnectionAborted).Returns(CancellationToken.None);
        _context.Setup(context => context.User).Returns(CreateUser(Guid.NewGuid()));

        _hub = new SlicerProgressHub(
            _logger.Object,
            _progressNotifier.Object,
            _jobRepository.Object,
            _resourceAccess.Object)
        {
            Context = _context.Object,
            Groups = _groups.Object,
        };
    }

    [Fact]
    public void Hub_RequiresAuthentication()
    {
        AuthorizeAttribute? attribute = typeof(SlicerProgressHub)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        _ = attribute.Should().NotBeNull();
    }

    [Fact]
    public async Task OnConnectedAsync_ForUser_AddsOnlyAuthenticatedUserGroup()
    {
        Guid userId = Guid.NewGuid();
        _context.Setup(context => context.User).Returns(CreateUser(userId));

        await _hub.OnConnectedAsync();

        _groups.Verify(group => group.AddToGroupAsync(
            "connection-1",
            AuthorizedHubGroups.User(userId),
            It.IsAny<CancellationToken>()), Times.Once);
        _groups.Verify(group => group.AddToGroupAsync(
            "connection-1",
            AuthorizedHubGroups.SlicingMonitors,
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnConnectedAsync_ForFarmAdministrator_AddsMonitoringGroup()
    {
        Guid userId = Guid.NewGuid();
        _context.Setup(context => context.User).Returns(CreateUser(
            userId,
            PrintFarmerPermissions.FarmAdminRole));

        await _hub.OnConnectedAsync();

        _groups.Verify(group => group.AddToGroupAsync(
            "connection-1",
            AuthorizedHubGroups.User(userId),
            It.IsAny<CancellationToken>()), Times.Once);
        _groups.Verify(group => group.AddToGroupAsync(
            "connection-1",
            AuthorizedHubGroups.SlicingMonitors,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubscribeToJobAsync_ForDifferentOwner_RejectsSubscription()
    {
        Guid jobId = Guid.NewGuid();
        SliceJob job = new() { Id = jobId, UserId = Guid.NewGuid() };
        _jobRepository
            .Setup(repository => repository.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        _resourceAccess
            .Setup(authorizer => authorizer.CanAccess(
                It.IsAny<ClaimsPrincipal>(),
                job.UserId,
                "slice-job-hub",
                job.Id))
            .Returns(false);

        Func<Task> act = () => _hub.SubscribeToJobAsync(jobId);

        _ = await act.Should().ThrowAsync<HubException>()
            .WithMessage("resource_forbidden");
        _progressNotifier.Verify(notifier => notifier.SubscribeToJobAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        _groups.Verify(group => group.AddToGroupAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SubscribeToJobAsync_ForOwner_AddsCanonicalJobGroup()
    {
        Guid jobId = Guid.NewGuid();
        SliceJob job = new() { Id = jobId, UserId = Guid.NewGuid() };
        _jobRepository
            .Setup(repository => repository.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        _resourceAccess
            .Setup(authorizer => authorizer.CanAccess(
                It.IsAny<ClaimsPrincipal>(),
                job.UserId,
                "slice-job-hub",
                job.Id))
            .Returns(true);

        await _hub.SubscribeToJobAsync(jobId);

        _progressNotifier.Verify(notifier => notifier.SubscribeToJobAsync(
            jobId,
            "connection-1",
            It.IsAny<CancellationToken>()), Times.Once);
        _groups.Verify(group => group.AddToGroupAsync(
            "connection-1",
            AuthorizedHubGroups.SliceJob(jobId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinUserGroupAsync_ForDifferentUser_RejectsSubscription()
    {
        Guid authenticatedUserId = Guid.NewGuid();
        _context.Setup(context => context.User).Returns(CreateUser(authenticatedUserId));

        Func<Task> act = () => _hub.JoinUserGroupAsync(Guid.NewGuid());

        _ = await act.Should().ThrowAsync<HubException>()
            .WithMessage("resource_forbidden");
    }

    [Fact]
    public async Task JoinMonitoringGroupAsync_ForNonAdministrator_RejectsSubscription()
    {
        Func<Task> act = () => _hub.JoinMonitoringGroupAsync();

        _ = await act.Should().ThrowAsync<HubException>()
            .WithMessage("resource_forbidden");
    }

    private static ClaimsPrincipal CreateUser(Guid userId, string? role = null)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(PrintFarmerPermissions.ClaimType, PrintFarmerPermissions.Queue.Read),
        ];
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
