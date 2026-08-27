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
using Farm.Infrastructure.Services.Users;
using Farm.Infrastructure.Telemetry;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Users;

public class UsersServiceTests
{
    private readonly Mock<IUsersRepository> _usersRepositoryMock;
    private readonly Mock<IAuthenticationService> _authenticationServiceMock;
    private readonly Mock<IPasswordHashingService> _passwordHashingServiceMock;
    private readonly Mock<IEffectivePermissionsRevocationService> _revocationServiceMock;
    private readonly Mock<IAuthAuditService> _authAuditServiceMock;
    private readonly IUsersService _usersService;
    private readonly CancellationToken _cancellationToken = CancellationToken.None;
    private readonly Guid _actorUserId = Guid.NewGuid();

    public UsersServiceTests()
    {
        _usersRepositoryMock = new Mock<IUsersRepository>(MockBehavior.Strict);
        _authenticationServiceMock = new Mock<IAuthenticationService>(MockBehavior.Strict);
        _passwordHashingServiceMock = new Mock<IPasswordHashingService>(MockBehavior.Strict);
        _revocationServiceMock = new Mock<IEffectivePermissionsRevocationService>(MockBehavior.Strict);
        _authAuditServiceMock = new Mock<IAuthAuditService>(MockBehavior.Strict);
        _usersService = new UsersService(
            _usersRepositoryMock.Object,
            _authenticationServiceMock.Object,
            _passwordHashingServiceMock.Object,
            _revocationServiceMock.Object,
            _authAuditServiceMock.Object);
    }

    #region GetUsersAsync Tests

    [Fact]
    public async Task GetUsersAsync_WithNoUsers_ReturnsEmptyList()
    {
        // Arrange
        var emptyList = new List<UserDto>();
        _usersRepositoryMock.Setup(x => x.GetUsersAsync(_cancellationToken))
            .ReturnsAsync(emptyList);

        // Act
        IReadOnlyList<UserDto> result = await _usersService.GetUsersAsync(_cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
        _usersRepositoryMock.Verify(x => x.GetUsersAsync(_cancellationToken), Times.Once);
    }

    [Fact]
    public async Task GetUsersAsync_WithMultipleUsers_ReturnsList()
    {
        // Arrange
        var userId1 = Guid.NewGuid();
        var userId2 = Guid.NewGuid();
        var users = new List<UserDto>
        {
            new UserDto { Id = userId1, Username = "user1", Email = "user1@example.com", FirstName = "John" },
            new UserDto { Id = userId2, Username = "user2", Email = "user2@example.com", FirstName = "Jane" }
        };
        _usersRepositoryMock.Setup(x => x.GetUsersAsync(_cancellationToken))
            .ReturnsAsync(users);

        // Act
        IReadOnlyList<UserDto> result = await _usersService.GetUsersAsync(_cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("user1", result[0].Username);
        Assert.Equal("user2", result[1].Username);
        _usersRepositoryMock.Verify(x => x.GetUsersAsync(_cancellationToken), Times.Once);
    }

    [Fact]
    public async Task GetUsersAsync_WhenRepositoryThrows_PropagatesException()
    {
        // Arrange
        _usersRepositoryMock.Setup(x => x.GetUsersAsync(_cancellationToken))
            .ThrowsAsync(new InvalidOperationException("Database error"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _usersService.GetUsersAsync(_cancellationToken));
    }

    #endregion

    #region CreateUserAsync Tests

    [Fact]
    public async Task CreateUserAsync_WithValidRequest_CreatesUserSuccessfully()
    {
        // Arrange
        var request = new CreateUserRequest
        {
            Username = "newuser",
            Email = "newuser@example.com",
            Password = "SecurePass123!",
            FirstName = "New",
            LastName = "User"
        };
        string passwordHash = "hashed_password_123";
        var userId = Guid.NewGuid();

        _passwordHashingServiceMock.Setup(x => x.HashPassword(request.Password))
            .Returns(passwordHash);
        _usersRepositoryMock.Setup(x => x.AddUserAsync(It.IsAny<User>(), It.IsAny<IEnumerable<Guid>>(), _cancellationToken))
            .Returns(Task.CompletedTask);
        _usersRepositoryMock.Setup(x => x.SaveChangesAsync(_cancellationToken))
            .Returns(Task.CompletedTask);

        var createdUserDto = new UserDto { Id = userId, Username = "newuser", Email = "newuser@example.com", FirstName = "New" };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(createdUserDto);

        // Act
        UserDto result = await _usersService.CreateUserAsync(request, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("newuser", result.Username);
        Assert.Equal("newuser@example.com", result.Email);
        _passwordHashingServiceMock.Verify(x => x.HashPassword(request.Password), Times.Once);
        _usersRepositoryMock.Verify(x => x.AddUserAsync(It.IsAny<User>(), It.IsAny<IEnumerable<Guid>>(), _cancellationToken), Times.Once);
        _usersRepositoryMock.Verify(x => x.SaveChangesAsync(_cancellationToken), Times.Once);
    }

    [Fact]
    public async Task CreateUserAsync_WithRoleIds_AssignsRolesToNewUser()
    {
        // Arrange
        var roleId = Guid.NewGuid();
        var request = new CreateUserRequest
        {
            Username = "admin",
            Email = "admin@example.com",
            Password = "SecurePass123!",
            RoleIds = new[] { roleId }
        };

        _passwordHashingServiceMock.Setup(x => x.HashPassword(request.Password))
            .Returns("hashed_password");
        _usersRepositoryMock.Setup(x => x.AddUserAsync(It.IsAny<User>(), It.IsAny<IEnumerable<Guid>>(), _cancellationToken))
            .Returns(Task.CompletedTask);
        _usersRepositoryMock.Setup(x => x.SaveChangesAsync(_cancellationToken))
            .Returns(Task.CompletedTask);

        var createdUser = new UserDto { Id = Guid.NewGuid(), Username = "admin", Email = "admin@example.com" };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(createdUser);

        // Act
        UserDto result = await _usersService.CreateUserAsync(request, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        _usersRepositoryMock.Verify(
            x => x.AddUserAsync(
                It.Is<User>(u => u.Username == "admin"),
                It.Is<IEnumerable<Guid>>(r => r == request.RoleIds),
                _cancellationToken),
            Times.Once);
    }

    [Fact]
    public async Task CreateUserAsync_WithoutRoleIds_CreatesUserWithoutRoles()
    {
        // Arrange
        var request = new CreateUserRequest
        {
            Username = "basicuser",
            Email = "basic@example.com",
            Password = "SecurePass123!"
        };

        _passwordHashingServiceMock.Setup(x => x.HashPassword(request.Password))
            .Returns("hashed");
        _usersRepositoryMock.Setup(x => x.AddUserAsync(It.IsAny<User>(), It.IsAny<IEnumerable<Guid>?>(), _cancellationToken))
            .Returns(Task.CompletedTask);
        _usersRepositoryMock.Setup(x => x.SaveChangesAsync(_cancellationToken))
            .Returns(Task.CompletedTask);

        var createdUser = new UserDto { Id = Guid.NewGuid(), Username = "basicuser" };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(createdUser);

        // Act
        UserDto result = await _usersService.CreateUserAsync(request, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        _usersRepositoryMock.Verify(x => x.AddUserAsync(It.IsAny<User>(), It.IsAny<IEnumerable<Guid>?>(), _cancellationToken), Times.Once);
    }

    [Fact]
    public async Task CreateUserAsync_WithMinimalFieldsOnly_CreatesSuccessfully()
    {
        // Arrange
        var request = new CreateUserRequest
        {
            Username = "minimal",
            Email = "minimal@example.com",
            Password = "Pass1!"
        };

        _passwordHashingServiceMock.Setup(x => x.HashPassword(request.Password))
            .Returns("hashed");
        _usersRepositoryMock.Setup(x => x.AddUserAsync(It.IsAny<User>(), It.IsAny<IEnumerable<Guid>?>(), _cancellationToken))
            .Returns(Task.CompletedTask);
        _usersRepositoryMock.Setup(x => x.SaveChangesAsync(_cancellationToken))
            .Returns(Task.CompletedTask);

        var createdUser = new UserDto { Id = Guid.NewGuid(), Username = "minimal", Email = "minimal@example.com" };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(createdUser);

        // Act
        UserDto result = await _usersService.CreateUserAsync(request, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Null(result.FirstName);
    }

    [Fact]
    public async Task CreateUserAsync_WhenAddUserThrows_PropagatesException()
    {
        // Arrange
        var request = new CreateUserRequest
        {
            Username = "testuser",
            Email = "test@example.com",
            Password = "Pass123!"
        };

        _passwordHashingServiceMock.Setup(x => x.HashPassword(request.Password))
            .Returns("hashed");
        _usersRepositoryMock.Setup(x => x.AddUserAsync(It.IsAny<User>(), It.IsAny<IEnumerable<Guid>?>(), _cancellationToken))
            .ThrowsAsync(new InvalidOperationException("User already exists"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _usersService.CreateUserAsync(request, _cancellationToken));
    }

    [Fact]
    public async Task CreateUserAsync_SetsDefaultValues_Correctly()
    {
        // Arrange
        var request = new CreateUserRequest
        {
            Username = "user",
            Email = "user@example.com",
            Password = "Pass123!"
        };

        _passwordHashingServiceMock.Setup(x => x.HashPassword(request.Password))
            .Returns("hashed");
        _usersRepositoryMock.Setup(x => x.AddUserAsync(It.IsAny<User>(), It.IsAny<IEnumerable<Guid>?>(), _cancellationToken))
            .Returns(Task.CompletedTask);
        _usersRepositoryMock.Setup(x => x.SaveChangesAsync(_cancellationToken))
            .Returns(Task.CompletedTask);

        var createdUser = new UserDto { Id = Guid.NewGuid(), Username = "user" };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(createdUser);

        // Act
        _ = await _usersService.CreateUserAsync(request, _cancellationToken);

        // Assert
        // Verify the user is created with correct defaults (checked via AddUserAsync call)
        _usersRepositoryMock.Verify(
            x => x.AddUserAsync(
                It.Is<User>(u => u.IsActive && !u.EmailConfirmed),
                It.IsAny<IEnumerable<Guid>?>(),
                _cancellationToken),
            Times.Once);
    }

    #endregion

    #region UpdateUserAsync Tests

    [Fact]
    public async Task UpdateUserAsync_WithValidId_UpdatesUserSuccessfully()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new UpdateUserRequest
        {
            FirstName = "Updated",
            LastName = "Name"
        };
        var existingUser = new User { Id = userId, Username = "user", Email = "user@example.com" };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync(existingUser);
        _usersRepositoryMock.Setup(x => x.SaveChangesAsync(_cancellationToken))
            .Returns(Task.CompletedTask);

        var updatedUserDto = new UserDto { Id = userId, Username = "user", FirstName = "Updated" };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(userId))
            .ReturnsAsync(updatedUserDto);

        // Act
        UserDto? result = await _usersService.UpdateUserAsync(userId, request, _actorUserId, ipAddress: null, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Updated", result.FirstName);
        _usersRepositoryMock.Verify(x => x.SaveChangesAsync(_cancellationToken), Times.Once);
    }

    [Fact]
    public async Task UpdateUserAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new UpdateUserRequest { FirstName = "Test" };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync((User?)null);

        // Act
        UserDto? result = await _usersService.UpdateUserAsync(userId, request, _actorUserId, ipAddress: null, _cancellationToken);

        // Assert
        Assert.Null(result);
        _usersRepositoryMock.Verify(x => x.SaveChangesAsync(_cancellationToken), Times.Never);
    }

    [Fact]
    public async Task UpdateUserAsync_WithOnlyFirstName_UpdatesOnlyFirstName()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new UpdateUserRequest { FirstName = "NewFirst" };
        var existingUser = new User { Id = userId, FirstName = "Old", LastName = "Last" };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync(existingUser);
        _usersRepositoryMock.Setup(x => x.SaveChangesAsync(_cancellationToken))
            .Returns(Task.CompletedTask);

        var updatedUser = new UserDto { Id = userId, FirstName = "NewFirst", LastName = "Last" };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(userId))
            .ReturnsAsync(updatedUser);

        // Act
        UserDto? result = await _usersService.UpdateUserAsync(userId, request, _actorUserId, ipAddress: null, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("NewFirst", result.FirstName);
        Assert.Equal("Last", result.LastName);
    }

    [Fact]
    public async Task UpdateUserAsync_WithIsActiveFalse_DeactivatesUser()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new UpdateUserRequest { IsActive = false };
        var existingUser = new User { Id = userId, IsActive = true };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync(existingUser);
        _usersRepositoryMock.Setup(x => x.SaveChangesAsync(_cancellationToken))
            .Returns(Task.CompletedTask);

        var updatedUser = new UserDto { Id = userId, IsActive = false };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(userId))
            .ReturnsAsync(updatedUser);

        // Act
        UserDto? result = await _usersService.UpdateUserAsync(userId, request, _actorUserId, ipAddress: null, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.IsActive);
    }

    [Fact]
    public async Task UpdateUserAsync_WithRoleIds_UpdatesRoles()
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
                It.Is<IReadOnlyList<string>>(added => added.SequenceEqual(new[] { "operator" })),
                It.Is<IReadOnlyList<string>>(removed => removed.Count == 0),
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
        _usersRepositoryMock.Verify(x => x.UpdateUserRolesAsync(userId, request.RoleIds, _cancellationToken), Times.Once);
        _revocationServiceMock.Verify(
            x => x.RevokeUsersAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == userId),
                _actorUserId,
                It.IsAny<string>(),
                null,
                _cancellationToken),
            Times.Once);
        _authAuditServiceMock.Verify(
            x => x.LogRoleAssignmentChangedAsync(
                _actorUserId,
                userId,
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                1,
                null,
                null,
                _cancellationToken),
            Times.Once);
    }

    [Fact]
    public async Task UpdateUserAsync_WithSameRoleIdsResubmitted_DoesNotRevokeSessions()
    {
        // Arrange -- resubmitting the user's existing role set is a no-op and must not
        // trigger a session revocation or audit event (only an actual role add/remove should).
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
        _revocationServiceMock.Verify(
            x => x.RevokeUsersAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _authAuditServiceMock.Verify(
            x => x.LogRoleAssignmentChangedAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateUserAsync_RemovingAllRoles_RevokesAndAuditsWithEmptyAddedList()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var request = new UpdateUserRequest { RoleIds = Array.Empty<Guid>() };
        var existingUser = new User { Id = userId };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync(existingUser);
        _usersRepositoryMock.Setup(x => x.UpdateUserRolesAsync(userId, request.RoleIds, _cancellationToken))
            .ReturnsAsync(new RoleAssignmentDiff(new List<Guid> { roleId }, new List<Guid>()));
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
                It.Is<IReadOnlyList<string>>(added => added.Count == 0),
                It.Is<IReadOnlyList<string>>(removed => removed.SequenceEqual(new[] { "operator" })),
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
        _authAuditServiceMock.Verify(
            x => x.LogRoleAssignmentChangedAsync(
                _actorUserId,
                userId,
                It.Is<IReadOnlyList<string>>(added => added.Count == 0),
                It.Is<IReadOnlyList<string>>(removed => removed.SequenceEqual(new[] { "operator" })),
                1,
                null,
                null,
                _cancellationToken),
            Times.Once);
    }

    [Fact]
    public async Task UpdateUserAsync_ReplacingOneRoleWithAnother_AuditsBothAddedAndRemoved()
    {
        // Arrange -- a single request that both adds and removes a role must report both lists.
        var userId = Guid.NewGuid();
        var oldRoleId = Guid.NewGuid();
        var newRoleId = Guid.NewGuid();
        var request = new UpdateUserRequest { RoleIds = new[] { newRoleId } };
        var existingUser = new User { Id = userId };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync(existingUser);
        _usersRepositoryMock.Setup(x => x.UpdateUserRolesAsync(userId, request.RoleIds, _cancellationToken))
            .ReturnsAsync(new RoleAssignmentDiff(new List<Guid> { oldRoleId }, new List<Guid> { newRoleId }));
        _usersRepositoryMock.Setup(x => x.GetRolesAsync(_cancellationToken))
            .ReturnsAsync(new List<RoleDto>
            {
                new() { Id = oldRoleId, Name = "operator" },
                new() { Id = newRoleId, Name = "viewer" }
            });

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
                It.Is<IReadOnlyList<string>>(added => added.SequenceEqual(new[] { "viewer" })),
                It.Is<IReadOnlyList<string>>(removed => removed.SequenceEqual(new[] { "operator" })),
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
        _authAuditServiceMock.Verify(
            x => x.LogRoleAssignmentChangedAsync(
                _actorUserId,
                userId,
                It.Is<IReadOnlyList<string>>(added => added.SequenceEqual(new[] { "viewer" })),
                It.Is<IReadOnlyList<string>>(removed => removed.SequenceEqual(new[] { "operator" })),
                1,
                null,
                null,
                _cancellationToken),
            Times.Once);
    }

    [Fact]
    public async Task UpdateUserAsync_WithInvalidRoleIdSilentlyDropped_DoesNotRevokeSessions()
    {
        // Arrange -- a request containing a role ID that doesn't correspond to a real Role is
        // silently dropped by the repository. The diff must be based on what was actually
        // persisted (re-read after the write), not on the raw request payload, so this must not
        // report a spurious "role added" or trigger a revocation when nothing effectively changed.
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var nonExistentRoleId = Guid.NewGuid();
        var request = new UpdateUserRequest { RoleIds = new[] { roleId, nonExistentRoleId } };
        var existingUser = new User { Id = userId };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync(existingUser);
        // The repository silently dropped nonExistentRoleId, so the persisted "after" set is
        // unchanged from "before" -- no revocation/audit should be triggered.
        _usersRepositoryMock.Setup(x => x.UpdateUserRolesAsync(userId, request.RoleIds, _cancellationToken))
            .ReturnsAsync(new RoleAssignmentDiff(new List<Guid> { roleId }, new List<Guid> { roleId }));

        var updatedUser = new UserDto { Id = userId };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(userId))
            .ReturnsAsync(updatedUser);

        // Act
        UserDto? result = await _usersService.UpdateUserAsync(userId, request, _actorUserId, ipAddress: null, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        _revocationServiceMock.Verify(
            x => x.RevokeUsersAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _authAuditServiceMock.Verify(
            x => x.LogRoleAssignmentChangedAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<int>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateUserAsync_WithWhitespaceOnlyFirstName_DoesNotUpdate()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var request = new UpdateUserRequest { FirstName = "   " };
        var existingUser = new User { Id = userId, FirstName = "Original" };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync(existingUser);
        _usersRepositoryMock.Setup(x => x.SaveChangesAsync(_cancellationToken))
            .Returns(Task.CompletedTask);

        var updatedUser = new UserDto { Id = userId, FirstName = "Original" };
        _authenticationServiceMock.Setup(x => x.GetUserWithRolesAndPermissionsAsync(userId))
            .ReturnsAsync(updatedUser);

        // Act
        UserDto? result = await _usersService.UpdateUserAsync(userId, request, _actorUserId, ipAddress: null, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Original", result.FirstName);
    }

    #endregion

    #region DeleteUserAsync Tests

    [Fact]
    public async Task DeleteUserAsync_WithValidId_DeletesUserSuccessfully()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var existingUser = new User { Id = userId, Username = "user" };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync(existingUser);
        _usersRepositoryMock.Setup(x => x.DeleteUserAsync(userId, _cancellationToken))
            .Returns(Task.CompletedTask);
        _usersRepositoryMock.Setup(x => x.SaveChangesAsync(_cancellationToken))
            .Returns(Task.CompletedTask);

        // Act
        bool result = await _usersService.DeleteUserAsync(userId, _cancellationToken);

        // Assert
        Assert.True(result);
        _usersRepositoryMock.Verify(x => x.DeleteUserAsync(userId, _cancellationToken), Times.Once);
        _usersRepositoryMock.Verify(x => x.SaveChangesAsync(_cancellationToken), Times.Once);
    }

    [Fact]
    public async Task DeleteUserAsync_WithNonExistentId_ReturnsFalse()
    {
        // Arrange
        var userId = Guid.NewGuid();

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync((User?)null);

        // Act
        bool result = await _usersService.DeleteUserAsync(userId, _cancellationToken);

        // Assert
        Assert.False(result);
        _usersRepositoryMock.Verify(x => x.DeleteUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteUserAsync_WhenDeleteThrows_PropagatesException()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var existingUser = new User { Id = userId };

        _usersRepositoryMock.Setup(x => x.GetUserEntityAsync(userId, _cancellationToken))
            .ReturnsAsync(existingUser);
        _usersRepositoryMock.Setup(x => x.DeleteUserAsync(userId, _cancellationToken))
            .ThrowsAsync(new InvalidOperationException("Cannot delete user"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _usersService.DeleteUserAsync(userId, _cancellationToken));
    }

    #endregion

    #region GetRolesAsync Tests

    [Fact]
    public async Task GetRolesAsync_ReturnsAllAvailableRoles()
    {
        // Arrange
        var roles = new List<RoleDto>
        {
            new RoleDto { Id = Guid.NewGuid(), Name = "farm_admin" },
            new RoleDto { Id = Guid.NewGuid(), Name = "printer_operator" },
            new RoleDto { Id = Guid.NewGuid(), Name = "viewer" }
        };

        _usersRepositoryMock.Setup(x => x.GetRolesAsync(_cancellationToken))
            .ReturnsAsync(roles);

        // Act
        IReadOnlyList<RoleDto> result = await _usersService.GetRolesAsync(_cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        _usersRepositoryMock.Verify(x => x.GetRolesAsync(_cancellationToken), Times.Once);
    }

    [Fact]
    public async Task GetRolesAsync_WithNoRoles_ReturnsEmptyList()
    {
        // Arrange
        _usersRepositoryMock.Setup(x => x.GetRolesAsync(_cancellationToken))
            .ReturnsAsync(new List<RoleDto>());

        // Act
        IReadOnlyList<RoleDto> result = await _usersService.GetRolesAsync(_cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    #endregion

    #region CheckAvailabilityAsync Tests

    [Fact]
    public async Task CheckAvailabilityAsync_WithUsernameOnly_ChecksUsernameAvailability()
    {
        // Arrange
        string username = "testuser";
        _usersRepositoryMock.Setup(x => x.UsernameExistsAsync(username, _cancellationToken))
            .ReturnsAsync(false);

        // Act
        UserAvailabilityDto result = await _usersService.CheckAvailabilityAsync(username, null, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.UsernameExists);
        Assert.Null(result.EmailExists);
        _usersRepositoryMock.Verify(x => x.UsernameExistsAsync(username, _cancellationToken), Times.Once);
    }

    [Fact]
    public async Task CheckAvailabilityAsync_WithEmailOnly_ChecksEmailAvailability()
    {
        // Arrange
        string email = "test@example.com";
        _usersRepositoryMock.Setup(x => x.EmailExistsAsync(email, _cancellationToken))
            .ReturnsAsync(true);

        // Act
        UserAvailabilityDto result = await _usersService.CheckAvailabilityAsync(null, email, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Null(result.UsernameExists);
        Assert.True(result.EmailExists);
    }

    [Fact]
    public async Task CheckAvailabilityAsync_WithBothUsernameAndEmail_ChecksBoth()
    {
        // Arrange
        string username = "testuser";
        string email = "test@example.com";
        _usersRepositoryMock.Setup(x => x.UsernameExistsAsync(username, _cancellationToken))
            .ReturnsAsync(false);
        _usersRepositoryMock.Setup(x => x.EmailExistsAsync(email, _cancellationToken))
            .ReturnsAsync(true);

        // Act
        UserAvailabilityDto result = await _usersService.CheckAvailabilityAsync(username, email, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.UsernameExists);
        Assert.True(result.EmailExists);
    }

    [Fact]
    public async Task CheckAvailabilityAsync_WithWhitespaceOnlyUsername_IgnoresUsername()
    {
        // Arrange
        _usersRepositoryMock.Setup(x => x.EmailExistsAsync("test@example.com", _cancellationToken))
            .ReturnsAsync(false);

        // Act
        UserAvailabilityDto result = await _usersService.CheckAvailabilityAsync("   ", "test@example.com", _cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Null(result.UsernameExists);
        Assert.False(result.EmailExists);
        _usersRepositoryMock.Verify(x => x.UsernameExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAvailabilityAsync_WithNullParameters_ReturnsNulls()
    {
        // Act
        UserAvailabilityDto result = await _usersService.CheckAvailabilityAsync(null, null, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Null(result.UsernameExists);
        Assert.Null(result.EmailExists);
    }

    [Fact]
    public async Task CheckAvailabilityAsync_TrimsWhitespace_BeforeChecking()
    {
        // Arrange
        string usernameWithSpaces = "  testuser  ";
        string trimmedUsername = "testuser";
        _usersRepositoryMock.Setup(x => x.UsernameExistsAsync(trimmedUsername, _cancellationToken))
            .ReturnsAsync(false);

        // Act
        UserAvailabilityDto result = await _usersService.CheckAvailabilityAsync(usernameWithSpaces, null, _cancellationToken);

        // Assert
        Assert.NotNull(result);
        _usersRepositoryMock.Verify(x => x.UsernameExistsAsync(trimmedUsername, _cancellationToken), Times.Once);
    }

    #endregion
}
