using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Shared;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerial")]
[TestTiming]
public class AuthAuditIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthAuditIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SuccessfulLogin_CreatesAuditLogEntry()
    {
        // Arrange - Create a user
        var registerRequest = new RegisterRequest(
            "auditloginuser",
            "auditlogin@test.com",
            "SecurePassword123!",
            "Audit",
            "User");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Approve the user
        var user = await context.Users.FirstAsync(u => u.Username == "auditloginuser");
        user.IsActive = true;
        await context.SaveChangesAsync();

        // Act - Login
        var loginRequest = new LoginRequest("auditloginuser", "SecurePassword123!");
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert - Check audit log
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var auditLogs = await verifyContext.AuthAuditLogs
            .Where(a => a.UserId == user.Id && a.EventType == AuthEventType.Login)
            .ToListAsync();

        auditLogs.Should().HaveCount(1);
        auditLogs[0].Success.Should().BeTrue();
        auditLogs[0].UserId.Should().Be(user.Id);
        auditLogs[0].EventType.Should().Be(AuthEventType.Login);
    }

    [Fact]
    public async Task FailedLogin_CreatesAuditLogEntry()
    {
        // Arrange - Create a user
        var registerRequest = new RegisterRequest(
            "auditfailuser",
            "auditfail@test.com",
            "SecurePassword123!",
            "Audit",
            "Fail");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await context.Users.FirstAsync(u => u.Username == "auditfailuser");
        user.IsActive = true;
        await context.SaveChangesAsync();

        // Act - Failed login with wrong password
        var loginRequest = new LoginRequest("auditfailuser", "WrongPassword123!");
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert - Check audit log for failed login
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var auditLogs = await verifyContext.AuthAuditLogs
            .Where(a => a.EventType == AuthEventType.LoginFailed)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync();

        auditLogs.Should().NotBeEmpty();
        var failedLogin = auditLogs.First();
        failedLogin.Success.Should().BeFalse();
        failedLogin.FailureReason.Should().Contain("Invalid password");
    }

    [Fact]
    public async Task Registration_CreatesAuditLogEntry()
    {
        // Arrange & Act - Register a new user
        var registerRequest = new RegisterRequest(
            $"auditreg{Guid.NewGuid():N}",
            $"auditreg{Guid.NewGuid():N}@test.com",
            "SecurePassword123!",
            "Audit",
            "Register");

        var response = await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        // Assert - Check audit log
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await context.Users
            .OrderByDescending(u => u.CreatedAt)
            .FirstAsync(u => u.FirstName == "Audit" && u.LastName == "Register");

        var auditLogs = await context.AuthAuditLogs
            .Where(a => a.UserId == user.Id && a.EventType == AuthEventType.Register)
            .ToListAsync();

        auditLogs.Should().HaveCount(1);
        auditLogs[0].Success.Should().BeTrue();
        auditLogs[0].EventType.Should().Be(AuthEventType.Register);
    }

    [Fact]
    public async Task PasswordChange_CreatesAuditLogEntry()
    {
        // Arrange - Create and login user
        var username = $"auditpwchange{Guid.NewGuid():N}";
        var email = $"{username}@test.com";

        var registerRequest = new RegisterRequest(
            username,
            email,
            "OldPassword123!",
            "Audit",
            "PwChange");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await context.Users.FirstAsync(u => u.Username == username);
        user.IsActive = true;
        await context.SaveChangesAsync();

        var loginRequest = new LoginRequest(username, "OldPassword123!");
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", loginRequest);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginResult!.Token);

        // Act - Change password
        var changePasswordRequest = new ChangePasswordRequest
        {
            CurrentPassword = "OldPassword123!",
            NewPassword = "NewPassword123!"
        };

        var response = await _client.PostAsJsonAsync("/api/auth/change-password", changePasswordRequest);

        // Assert - Check audit log
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var auditLogs = await verifyContext.AuthAuditLogs
            .Where(a => a.UserId == user.Id && a.EventType == AuthEventType.PasswordChange)
            .ToListAsync();

        auditLogs.Should().HaveCount(1);
        auditLogs[0].Success.Should().BeTrue();
        auditLogs[0].EventType.Should().Be(AuthEventType.PasswordChange);
    }

    [Fact]
    public async Task PasswordResetInitiated_CreatesAuditLogEntry()
    {
        // Arrange - Create user
        var email = $"auditreset{Guid.NewGuid():N}@test.com";

        var registerRequest = new RegisterRequest(
            $"auditreset{Guid.NewGuid():N}",
            email,
            "SecurePassword123!",
            "Audit",
            "Reset");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        // Act - Initiate password reset
        var forgotPasswordRequest = new Farm.Web.Shared.Contracts.Auth.ForgotPasswordRequest
        {
            Email = email
        };
        var response = await _client.PostAsJsonAsync("/api/auth/forgot-password", forgotPasswordRequest);

        // Assert - Check audit log
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var auditLogs = await context.AuthAuditLogs
            .Where(a => a.EventType == AuthEventType.PasswordResetInitiated)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync();

        auditLogs.Should().NotBeEmpty();
        var resetLog = auditLogs.First();
        resetLog.Success.Should().BeTrue();
        resetLog.Metadata.Should().Contain(email);
    }

    [Fact]
    public async Task PasswordResetCompleted_CreatesAuditLogEntry()
    {
        // Arrange - Create user and reset token
        var username = $"auditresetcomp{Guid.NewGuid():N}";
        var email = $"{username}@test.com";

        var registerRequest = new RegisterRequest(
            username,
            email,
            "OldPassword123!",
            "Audit",
            "ResetComp");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHashingService = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        var user = await context.Users.FirstAsync(u => u.Username == username);
        user.IsActive = true;

        // Create password reset token
        var resetToken = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "test-reset-token-123",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            IsUsed = false
        };

        context.PasswordResetTokens.Add(resetToken);
        await context.SaveChangesAsync();

        // Act - Complete password reset
        var resetPasswordRequest = new Farm.Web.Shared.Contracts.Auth.ResetPasswordRequest
        {
            Token = "test-reset-token-123",
            Email = email,
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        };

        var response = await _client.PostAsJsonAsync("/api/auth/reset-password", resetPasswordRequest);

        // Assert - Check audit log
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var auditLogs = await verifyContext.AuthAuditLogs
            .Where(a => a.UserId == user.Id && a.EventType == AuthEventType.PasswordReset)
            .ToListAsync();

        auditLogs.Should().HaveCount(1);
        auditLogs[0].Success.Should().BeTrue();
        auditLogs[0].EventType.Should().Be(AuthEventType.PasswordReset);
    }

    [Fact]
    public async Task AccountLocked_CreatesAuditLogEntry()
    {
        // Arrange - Create user
        var username = $"auditlockuser{Guid.NewGuid():N}";
        var email = $"{username}@test.com";

        var registerRequest = new RegisterRequest(
            username,
            email,
            "SecurePassword123!",
            "Audit",
            "Lock");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await context.Users.FirstAsync(u => u.Username == username);
        user.IsActive = true;
        await context.SaveChangesAsync();

        // Act - Trigger account lockout with 5 failed login attempts
        for (int i = 0; i < 5; i++)
        {
            var loginRequest = new LoginRequest(username, "WrongPassword!");
            await _client.PostAsJsonAsync("/api/auth/login", loginRequest);
        }

        // Assert - Check audit log for account locked event
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        var auditLogs = await verifyContext.AuthAuditLogs
            .Where(a => a.UserId == user.Id && a.EventType == AuthEventType.AccountLocked)
            .ToListAsync();

        auditLogs.Should().HaveCount(1);
        auditLogs[0].Success.Should().BeTrue();
        auditLogs[0].Metadata.Should().Contain("5"); // Should contain attempt count
        auditLogs[0].Metadata.Should().Contain("LockoutDurationMinutes"); // Should contain lockout duration
    }

    [Fact]
    public async Task GetUserAuditLog_ReturnsUserEvents()
    {
        // Arrange - Create user and perform actions
        var username = $"auditgetlog{Guid.NewGuid():N}";
        var email = $"{username}@test.com";

        var registerRequest = new RegisterRequest(
            username,
            email,
            "SecurePassword123!",
            "Audit",
            "GetLog");

        await _client.PostAsJsonAsync("/api/auth/register", registerRequest);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await context.Users.FirstAsync(u => u.Username == username);
        user.IsActive = true;
        await context.SaveChangesAsync();

        // Perform login
        var loginRequest = new LoginRequest(username, "SecurePassword123!");
        await _client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Act - Get audit log via service
        var auditService = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var auditLogs = await auditService.GetUserAuditLogAsync(user.Id, pageSize: 10);

        // Assert
        auditLogs.Should().HaveCountGreaterOrEqualTo(2); // Register + Login
        auditLogs.Should().Contain(log => log.EventType == AuthEventType.Register);
        auditLogs.Should().Contain(log => log.EventType == AuthEventType.Login);
        auditLogs.Should().OnlyContain(log => log.UserId == user.Id);
    }
}
