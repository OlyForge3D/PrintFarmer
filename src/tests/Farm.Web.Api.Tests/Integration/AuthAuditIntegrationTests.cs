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

    private static async Task DumpResponse(HttpResponseMessage resp, string tag)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[TestHttp][{tag}] Status={(int)resp.StatusCode} {resp.StatusCode}");
            Console.WriteLine($"[TestHttp][{tag}] Body={body}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TestHttp][{tag}] Dump failed: {ex}");
        }
    }

    private static void DumpAuthAuditInfo(Farm.Infrastructure.Data.AppDbContext ctx, string tag = "verify")
    {
        try
        {
            Console.WriteLine($"[TestDiag][{tag}] Provider={ctx.Database.ProviderName}");
            try
            { Console.WriteLine($"[TestDiag][{tag}] Conn={ctx.Database.GetConnectionString()}"); }
            catch { }
            var conn = ctx.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            { conn.Open(); }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM \"AuthAuditLogs\"";
            var res = cmd.ExecuteScalar();
            Console.WriteLine($"[TestDiag][{tag}] AuthAuditLogs count={res}");

            // Print up to 10 sample rows for quick inspection
            using var sampleCmd = conn.CreateCommand();
            sampleCmd.CommandText = "SELECT Id, UserId, EventType, FailureReason, Metadata FROM \"AuthAuditLogs\" ORDER BY Timestamp DESC LIMIT 10";
            using var rdr = sampleCmd.ExecuteReader();
            Console.WriteLine($"[TestDiag][{tag}] AuthAuditLogs sample:");
            while (rdr.Read())
            {
                var id = rdr.IsDBNull(0) ? "(null)" : rdr.GetValue(0).ToString();
                var uid = rdr.IsDBNull(1) ? "(null)" : rdr.GetValue(1).ToString();
                var et = rdr.IsDBNull(2) ? "(null)" : rdr.GetValue(2).ToString();
                var fr = rdr.IsDBNull(3) ? "(null)" : rdr.GetValue(3).ToString();
                var md = rdr.IsDBNull(4) ? "(null)" : rdr.GetValue(4).ToString();
                Console.WriteLine($" - Id={id} UserId={uid} EventType={et} FailureReason={fr} Metadata={md}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TestDiag][{tag}] Dump failed: {ex}");
        }
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

        var registerResponse = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });
        // Dump HTTP response for diagnostics
        await DumpResponse(registerResponse, "registerResponse");

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Approve the user
        var user = await context.Users.FirstAsync(u => u.Username == "auditloginuser");
        user.IsActive = true;
        await context.SaveChangesAsync();

        // Act - Login
        var loginRequest = new LoginRequest("auditloginuser", "SecurePassword123!");
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { UsernameOrEmail = loginRequest.Username, Password = loginRequest.Password });
        // Dump HTTP response for diagnostics
        await DumpResponse(loginResponse, "loginResponse");

        // Assert - Check audit log
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        DumpAuthAuditInfo(verifyContext, "SuccessfulLogin");

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

        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await context.Users.FirstAsync(u => u.Username == "auditfailuser");
        user.IsActive = true;
        await context.SaveChangesAsync();

        // Act - Failed login with wrong password
        var loginRequest = new LoginRequest("auditfailuser", "WrongPassword123!");
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { UsernameOrEmail = loginRequest.Username, Password = loginRequest.Password });

        // Assert - Check audit log for failed login
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        DumpAuthAuditInfo(verifyContext, "FailedLogin");

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

        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        // Assert - Check audit log
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await context.Users
            .OrderByDescending(u => u.CreatedAt)
            .FirstAsync(u => u.FirstName == "Audit" && u.LastName == "Register");

        DumpAuthAuditInfo(context, "Registration");

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

        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await context.Users.FirstAsync(u => u.Username == username);
        user.IsActive = true;
        await context.SaveChangesAsync();

        // Act - Change password by calling the service directly (avoids controller model binding subtleties in tests)
        var authService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Authentication.IAuthenticationService>();
        var success = await authService.ChangePasswordAsync(user.Id, "OldPassword123!", "NewPassword123!");
        success.Should().BeTrue();

        // Assert - Check audit log
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        DumpAuthAuditInfo(verifyContext, "PasswordChange");

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

        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        // Act - Initiate password reset
        var forgotPasswordRequest = new Farm.Web.Shared.Contracts.Auth.ForgotPasswordRequest
        {
            Email = email
        };
        var response = await _client.PostAsJsonAsync("/api/auth/forgot-password", forgotPasswordRequest);

        // Assert - Check audit log
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        DumpAuthAuditInfo(context, "PasswordResetInitiated");

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

        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

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

        DumpAuthAuditInfo(verifyContext, "PasswordResetCompleted");

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

        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await context.Users.FirstAsync(u => u.Username == username);
        user.IsActive = true;
        await context.SaveChangesAsync();

        // Act - Trigger account lockout with 5 failed login attempts
        for (int i = 0; i < 5; i++)
        {
            var loginRequest = new LoginRequest(username, "WrongPassword!");
            await _client.PostAsJsonAsync("/api/auth/login", new { UsernameOrEmail = loginRequest.Username, Password = loginRequest.Password });
        }

        // Assert - Check audit log for account locked event
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        DumpAuthAuditInfo(verifyContext, "AccountLocked");

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

        await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await context.Users.FirstAsync(u => u.Username == username);
        user.IsActive = true;
        await context.SaveChangesAsync();

        // Perform login
        var loginRequest = new LoginRequest(username, "SecurePassword123!");
        await _client.PostAsJsonAsync("/api/auth/login", new { UsernameOrEmail = loginRequest.Username, Password = loginRequest.Password });

        // Act - Get audit log via service
        var auditService = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        var auditLogs = await auditService.GetUserAuditLogAsync(user.Id, pageSize: 10);

        DumpAuthAuditInfo(context, "GetUserAuditLog");

        // Assert
        auditLogs.Should().HaveCountGreaterOrEqualTo(2); // Register + Login
        auditLogs.Should().Contain(log => log.EventType == AuthEventType.Register);
        auditLogs.Should().Contain(log => log.EventType == AuthEventType.Login);
        auditLogs.Should().OnlyContain(log => log.UserId == user.Id);
    }
}
