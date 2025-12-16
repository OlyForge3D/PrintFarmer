using System;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Authentication;
using Farm.Infrastructure.Services.Authentication;
using Farm.Web.Api.Services.Authentication;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for AuthAuditService
/// Tests authentication audit logging and repository functionality
/// </summary>
public class AuthAuditServiceIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthAuditServiceIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
    }

    /// <summary>
    /// Creates a test user in the database for audit log foreign key compliance
    /// </summary>
    private async Task<User> CreateTestUserAsync(string username = "testuser")
    {
        using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = "hash",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task LogLoginAsync_WithValidParameters_SavesAndRetrievesAuditLog()
    {
        // Arrange
        var user = await CreateTestUserAsync("login-test-user");
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();
        var ipAddress = "192.168.1.100";
        var correlationId = "test-correlation-123";

        // Act
        await service.LogLoginAsync(user.Id, ipAddress, "Mozilla/5.0", correlationId);

        // Assert
        var logs = await repository.GetByUserIdAsync(user.Id);
        logs.Should().NotBeEmpty();
        logs.Should().ContainSingle(l => l.EventType == AuthEventType.Login && l.CorrelationId == correlationId);
        logs[0].IpAddress.Should().Be(ipAddress);
        logs[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task LogLoginAsync_MultipleLogins_StoresAllEvents()
    {
        // Arrange
        var user = await CreateTestUserAsync("multi-login-user");
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();

        // Act
        await service.LogLoginAsync(user.Id, "192.168.1.1", "Chrome", null);
        await service.LogLoginAsync(user.Id, "192.168.1.2", "Firefox", null);
        await service.LogLoginAsync(user.Id, "192.168.1.3", "Safari", null);

        // Assert
        var logs = await repository.GetByUserIdAsync(user.Id, pageSize: 10);
        logs.Should().HaveCount(3);
        logs.Should().AllSatisfy(l => l.EventType.Should().Be(AuthEventType.Login));
    }

    [Fact]
    public async Task LogLoginFailedAsync_WithValidParameters_SavesFailureLog()
    {
        // Arrange
        var username = "loginfail-test@example.com";
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();

        // Act
        await service.LogLoginFailedAsync(username, "Invalid password", "192.168.1.100", null, null);

        // Assert
        var logs = await repository.GetRecentFailedLoginsAsync(count: 100);
        logs.Where(l => l.EventType == AuthEventType.LoginFailed && l.FailureReason == "Invalid password").Should().HaveCountGreaterThanOrEqualTo(1);
        logs.Should().Contain(l => l.EventType == AuthEventType.LoginFailed && l.FailureReason == "Invalid password");
        logs.Where(l => l.EventType == AuthEventType.LoginFailed && l.FailureReason == "Invalid password").First().Success.Should().BeFalse();
    }

    [Fact]
    public async Task LogLoginFailedAsync_MultipleFailures_AllStored()
    {
        // Arrange
        var username = "multifail-test@example.com";
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();

        // Act
        await service.LogLoginFailedAsync(username, "Invalid password", "192.168.1.1", null, null);
        await service.LogLoginFailedAsync(username, "Invalid password", "192.168.1.2", null, null);
        await service.LogLoginFailedAsync(username, "Invalid password", "192.168.1.3", null, null);

        // Assert
        var logs = await repository.GetRecentFailedLoginsAsync(count: 10);
        var failedLogins = logs.Where(l => l.EventType == AuthEventType.LoginFailed && l.FailureReason == "Invalid password").ToList();
        failedLogins.Should().HaveCountGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task LogLogoutAsync_WithValidParameters_SavesLogoutLog()
    {
        // Arrange
        var user = await CreateTestUserAsync("logout-test-user");
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();

        // Act
        await service.LogLogoutAsync(user.Id, "192.168.1.100", "Chrome", null);

        // Assert
        var logs = await repository.GetByUserIdAsync(user.Id);
        logs.Should().ContainSingle(l => l.EventType == AuthEventType.Logout);
        logs[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task LogPasswordChangeAsync_WithValidParameters_SavesPasswordChangeLog()
    {
        // Arrange
        var user = await CreateTestUserAsync("pwdchange-test-user");
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();

        // Act
        await service.LogPasswordChangeAsync(user.Id, "192.168.1.100", "Firefox", null);

        // Assert
        var logs = await repository.GetByUserIdAsync(user.Id);
        logs.Should().ContainSingle(l => l.EventType == AuthEventType.PasswordChange);
        logs[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task LogRegisterAsync_WithValidParameters_SavesRegistrationLog()
    {
        // Arrange
        var user = await CreateTestUserAsync("register-test-user");
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();

        // Act
        await service.LogRegisterAsync(user.Id, "192.168.1.100", "Safari", null);

        // Assert
        var logs = await repository.GetByUserIdAsync(user.Id);
        logs.Should().ContainSingle(l => l.EventType == AuthEventType.Register);
        logs[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task LogAccountLockedAsync_WithValidParameters_SavesLockoutLog()
    {
        // Arrange
        var user = await CreateTestUserAsync("locked-test-user");
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();
        var lockoutDuration = TimeSpan.FromMinutes(15);

        // Act
        await service.LogAccountLockedAsync(user.Id, 5, lockoutDuration, "192.168.1.100", null);

        // Assert
        var logs = await repository.GetByUserIdAsync(user.Id);
        logs.Should().ContainSingle(l => l.EventType == AuthEventType.AccountLocked);
        logs[0].Metadata.Should().Contain("15"); // Duration should be in metadata
    }

    [Fact]
    public async Task LogAccountUnlockedAsync_WithValidParameters_SavesUnlockLog()
    {
        // Arrange
        var user = await CreateTestUserAsync("unlocked-test-user");
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();

        // Act
        await service.LogAccountUnlockedAsync(user.Id, "Admin unlock", "192.168.1.100", null);

        // Assert
        var logs = await repository.GetByUserIdAsync(user.Id);
        logs.Should().ContainSingle(l => l.EventType == AuthEventType.AccountUnlocked);
        logs[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task LogRefreshTokenAsync_WithValidParameters_SavesRefreshLog()
    {
        // Arrange
        var user = await CreateTestUserAsync("refresh-test-user");
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();

        // Act
        await service.LogRefreshTokenAsync(user.Id, "192.168.1.100", null);

        // Assert
        var logs = await repository.GetByUserIdAsync(user.Id);
        logs.Should().ContainSingle(l => l.EventType == AuthEventType.RefreshToken);
        logs[0].Success.Should().BeTrue();
    }

    [Fact]
    public async Task LogTokenRevokedAsync_WithValidParameters_SavesRevocationLog()
    {
        // Arrange
        var user = await CreateTestUserAsync("revoked-test-user");
        var adminUser = await CreateTestUserAsync("admin-revoke-user");
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();

        // Act
        await service.LogTokenRevokedAsync(user.Id, adminUser.Id, "Security incident", "192.168.1.100", null);

        // Assert
        var logs = await repository.GetByUserIdAsync(user.Id);
        logs.Should().ContainSingle(l => l.EventType == AuthEventType.TokenRevoked);
        logs[0].Metadata.Should().Contain(adminUser.Id.ToString());
    }

    [Fact]
    public async Task GetUserAuditLogAsync_WithValidUserId_ReturnsPagedLogs()
    {
        // Arrange
        var user = await CreateTestUserAsync("paged-logs-user");
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();

        // Create multiple audit log entries
        await service.LogLoginAsync(user.Id, "192.168.1.1", "Chrome", null);
        await service.LogLogoutAsync(user.Id, "192.168.1.1", "Chrome", null);
        await service.LogPasswordChangeAsync(user.Id, "192.168.1.1", "Chrome", null);

        // Act
        var logs = await service.GetUserAuditLogAsync(user.Id, pageSize: 50, pageNumber: 1);

        // Assert
        logs.Should().HaveCountGreaterThanOrEqualTo(3);
        logs.Should().AllSatisfy(l => l.UserId.Should().Be(user.Id));
    }

    [Fact]
    public async Task GetSecurityEventsAsync_ReturnsSecurityRelatedEvents()
    {
        // Arrange
        var user = await CreateTestUserAsync("security-events-user");
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();

        // Create various security events
        await service.LogAccountLockedAsync(user.Id, 5, TimeSpan.FromMinutes(15), null, null);
        await service.LogAccountUnlockedAsync(user.Id, "Unlocked", null, null);

        // Act
        var events = await service.GetSecurityEventsAsync(since: DateTime.UtcNow.AddHours(-1));

        // Assert - Just verify events exist for this user, since other tests may have run
        events.Should().NotBeEmpty();
        events.Where(e => e.UserId == user.Id && e.EventType == AuthEventType.AccountLocked).Should().HaveCountGreaterThanOrEqualTo(1);
        events.Where(e => e.UserId == user.Id && e.EventType == AuthEventType.AccountUnlocked).Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task CountRecentFailedLoginsAsync_WithMultipleFailures_CountsCorrectly()
    {
        // Arrange
        using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var repository = scope.ServiceProvider.GetRequiredService<IAuthAuditLogRepository>();
        var timeWindow = TimeSpan.FromHours(1);

        // Count should be at least the ones we created
        await service.LogLoginFailedAsync("user1@test.com", "Invalid", null, null, null);
        await service.LogLoginFailedAsync("user2@test.com", "Invalid", null, null, null);

        // Act
        var count = await repository.CountRecentFailedLoginsAsync(null, timeWindow);

        // Assert
        count.Should().BeGreaterThanOrEqualTo(2);
    }
}
