using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Users;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.Authentication;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Users;

/// <summary>
/// #1731: verifies UsersService notifies IQueueSubscriptionMembershipNotifier exactly once
/// when a user's role assignment actually changes (a role change can alter which printers/
/// resources the user is authorized to subscribe to over SignalR), and does not notify when
/// the resubmitted role set is unchanged.
/// </summary>
public class UsersServiceMembershipNotificationTests
{
    private readonly Mock<IUsersRepository> _usersRepositoryMock;
    private readonly Mock<IAuthenticationService> _authenticationServiceMock;
    private readonly Mock<IPasswordHashingService> _passwordHashingServiceMock;
    private readonly Mock<IEffectivePermissionsRevocationService> _revocationServiceMock;
    private readonly Mock<IAuthAuditService> _authAuditServiceMock;
    private readonly Mock<IQueueSubscriptionMembershipNotifier> _membershipNotifierMock;
    private readonly IUsersService _usersService;
    private readonly CancellationToken _cancellationToken = CancellationToken.None;
    private readonly Guid _actorUserId = Guid.NewGuid();

    public UsersServiceMembershipNotificationTests()
    {
        _usersRepositoryMock = new Mock<IUsersRepository>(MockBehavior.Strict);
        _authenticationServiceMock = new Mock<IAuthenticationService>(MockBehavior.Strict);
        _passwordHashingServiceMock = new Mock<IPasswordHashingService>(MockBehavior.Strict);
        _revocationServiceMock = new Mock<IEffectivePermissionsRevocationService>(MockBehavior.Strict);
        _authAuditServiceMock = new Mock<IAuthAuditService>(MockBehavior.Strict);
        _membershipNotifierMock = new Mock<IQueueSubscriptionMembershipNotifier>(MockBehavior.Strict);
        _membershipNotifierMock
            .Setup(x => x.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _usersService = new UsersService(
            _usersRepositoryMock.Object,
            _authenticationServiceMock.Object,
            _passwordHashingServiceMock.Object,
            _revocationServiceMock.Object,
            _authAuditServiceMock.Object,
            _membershipNotifierMock.Object);
    }

    [Fact]
    public async Task UpdateUserAsync_WithRoleIds_NotifiesMembershipChangedExactlyOnce()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var request = new UpdateUserRequest { RoleIds = new[] { roleId } };
        var existingUser = new User { Id = userId };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync(existingUser);
        _usersRepositoryMock.Setup(x => x.UpdateUserRolesAsync(userId, request.RoleIds, _cancellationToken))
            .ReturnsAsync(new RoleAssignmentDiff(new List<Guid>(), new List<Guid> { roleId }));
        _usersRepositoryMock.Setup(x => x.GetRolesAsync(_cancellationToken))
            .ReturnsAsync(new List<RoleDto> { new() { Id = roleId, Name = "operator" } });

        _revocationServiceMock
            .Setup(x => x.RevokeUsersAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == userId),
                _actorUserId,
                It.IsAny<string>(),
                null,
                _cancellationToken))
            .ReturnsAsync(1);
        _authAuditServiceMock
            .Setup(x => x.LogRoleAssignmentChangedAsync(
                _actorUserId,
                userId,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                1,
                null,
                null,
                _cancellationToken))
            .Returns(Task.CompletedTask);

        var updatedUser = new UserDto { Id = userId };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(userId))
            .ReturnsAsync(updatedUser);

        // Act
        UserDto? result = await _usersService.UpdateUserAsync(userId, request, _actorUserId, ipAddress: null, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        _membershipNotifierMock.Verify(x => x.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateUserAsync_WithSameRoleIdsResubmitted_DoesNotNotifyMembershipChanged()
    {
        // Arrange -- resubmitting the user's existing role set is a no-op and cannot change
        // subscription authorization, so it must not trigger a membership-change notification.
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var request = new UpdateUserRequest { RoleIds = new[] { roleId } };
        var existingUser = new User { Id = userId };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync(existingUser);
        _usersRepositoryMock.Setup(x => x.UpdateUserRolesAsync(userId, request.RoleIds, _cancellationToken))
            .ReturnsAsync(new RoleAssignmentDiff(new List<Guid> { roleId }, new List<Guid> { roleId }));

        var updatedUser = new UserDto { Id = userId };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(userId))
            .ReturnsAsync(updatedUser);

        // Act
        UserDto? result = await _usersService.UpdateUserAsync(userId, request, _actorUserId, ipAddress: null, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        _membershipNotifierMock.Verify(x => x.NotifyMembershipChangedAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
