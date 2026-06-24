using System;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

[Trait("Category", "DbHeavy")]
[Collection(IntegrationTestCollection.Name)]
[TestTiming]
public class AuthAuditIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _client = null!;

    public AuthAuditIntegrationTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _client = await _factory.CreateAuthenticatedClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private static async Task DumpResponse(HttpResponseMessage resp, string tag)
    {
        try
        {
            string body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"[TestHttp][{tag}] Status={(int)resp.StatusCode} {resp.StatusCode}");
            Console.WriteLine($"[TestHttp][{tag}] Body={body}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TestHttp][{tag}] Dump failed: {ex}");
        }
    }

    private static void DumpAuthAuditInfo(AppDbContext ctx, string tag = "verify")
    {
        try
        {
            Console.WriteLine($"[TestDiag][{tag}] Provider={ctx.Database.ProviderName}");
            try
            { Console.WriteLine($"[TestDiag][{tag}] Conn={ctx.Database.GetConnectionString()}"); }
            catch { }
            DbConnection conn = ctx.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
            { conn.Open(); }
            using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM \"AuthAuditLogs\"";
            object? res = cmd.ExecuteScalar();
            Console.WriteLine($"[TestDiag][{tag}] AuthAuditLogs count={res}");

            // Print up to 10 sample rows for quick inspection
            using DbCommand sampleCmd = conn.CreateCommand();
            sampleCmd.CommandText = "SELECT Id, UserId, EventType, FailureReason, Metadata FROM \"AuthAuditLogs\" ORDER BY Timestamp DESC LIMIT 10";
            using DbDataReader rdr = sampleCmd.ExecuteReader();
            Console.WriteLine($"[TestDiag][{tag}] AuthAuditLogs sample:");
            while (rdr.Read())
            {
                string? id = rdr.IsDBNull(0) ? "(null)" : rdr.GetValue(0).ToString();
                string? uid = rdr.IsDBNull(1) ? "(null)" : rdr.GetValue(1).ToString();
                string? et = rdr.IsDBNull(2) ? "(null)" : rdr.GetValue(2).ToString();
                string? fr = rdr.IsDBNull(3) ? "(null)" : rdr.GetValue(3).ToString();
                string? md = rdr.IsDBNull(4) ? "(null)" : rdr.GetValue(4).ToString();
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
        RegisterRequest registerRequest = new RegisterRequest
        {
            Username = "auditloginuser",
            Email = "auditlogin@test.com",
            Password = "SecurePassword123!",
            ConfirmPassword = "SecurePassword123!",
            FirstName = "Audit",
            LastName = "User"
        };

        HttpResponseMessage registerResponse = await _client.PostAsJsonAsync("/api/auth/register", new
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

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Approve the user
        User user = await context.Users.FirstAsync(u => u.Username == "auditloginuser");
        user.IsActive = true;
        _ = await context.SaveChangesAsync();

        // Act - Login
        LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = "auditloginuser", Password = "SecurePassword123!" };
        HttpResponseMessage loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { UsernameOrEmail = loginRequest.UsernameOrEmail, Password = loginRequest.Password });
        // Dump HTTP response for diagnostics
        await DumpResponse(loginResponse, "loginResponse");

        // Assert - Check audit log
        using IServiceScope verifyScope = _factory.Services.CreateScope();
        AppDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        DumpAuthAuditInfo(verifyContext, "SuccessfulLogin");

        List<AuthAuditLog> auditLogs = await verifyContext.AuthAuditLogs
            .Where(a => a.UserId == user.Id && a.EventType == AuthEventType.Login)
            .ToListAsync();

        _ = auditLogs.Should().HaveCount(1);
        _ = auditLogs[0].Success.Should().BeTrue();
        _ = auditLogs[0].UserId.Should().Be(user.Id);
        _ = auditLogs[0].EventType.Should().Be(AuthEventType.Login);
    }

    [Fact]
    public async Task FailedLogin_CreatesAuditLogEntry()
    {
        // Arrange - Create a user
        RegisterRequest registerRequest = new RegisterRequest
        {
            Username = "auditfailuser",
            Email = "auditfail@test.com",
            Password = "SecurePassword123!",
            ConfirmPassword = "SecurePassword123!",
            FirstName = "Audit",
            LastName = "Fail"
        };

        _ = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        User user = await context.Users.FirstAsync(u => u.Username == "auditfailuser");
        user.IsActive = true;
        _ = await context.SaveChangesAsync();

        // Act - Failed login with wrong password
        LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = "auditfailuser", Password = "WrongPassword123!" };
        HttpResponseMessage loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new { UsernameOrEmail = loginRequest.UsernameOrEmail, Password = loginRequest.Password });

        // Assert - Check audit log for failed login
        using IServiceScope verifyScope = _factory.Services.CreateScope();
        AppDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        DumpAuthAuditInfo(verifyContext, "FailedLogin");

        List<AuthAuditLog> auditLogs = await verifyContext.AuthAuditLogs
            .Where(a => a.EventType == AuthEventType.LoginFailed)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync();

        _ = auditLogs.Should().NotBeEmpty();
        AuthAuditLog failedLogin = auditLogs.First();
        _ = failedLogin.Success.Should().BeFalse();
        _ = failedLogin.FailureReason.Should().Contain("Invalid password");
    }

    [Fact]
    public async Task Registration_CreatesAuditLogEntry()
    {
        // Arrange & Act - Register a new user
        RegisterRequest registerRequest = new RegisterRequest
        {
            Username = $"auditreg{Guid.NewGuid():N}",
            Email = $"auditreg{Guid.NewGuid():N}@test.com",
            Password = "SecurePassword123!",
            ConfirmPassword = "SecurePassword123!",
            FirstName = "Audit",
            LastName = "Register"
        };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        // Assert - Check audit log
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        User user = await context.Users
            .OrderByDescending(u => u.CreatedAt)
            .FirstAsync(u => u.FirstName == "Audit" && u.LastName == "Register");

        DumpAuthAuditInfo(context, "Registration");

        List<AuthAuditLog> auditLogs = await context.AuthAuditLogs
            .Where(a => a.UserId == user.Id && a.EventType == AuthEventType.Register)
            .ToListAsync();

        _ = auditLogs.Should().HaveCount(1);
        _ = auditLogs[0].Success.Should().BeTrue();
        _ = auditLogs[0].EventType.Should().Be(AuthEventType.Register);
    }

    [Fact]
    public async Task PasswordChange_CreatesAuditLogEntry()
    {
        // Arrange - Create and login user
        string username = $"auditpwchange{Guid.NewGuid():N}";
        string email = $"{username}@test.com";

        RegisterRequest registerRequest = new RegisterRequest
        {
            Username = username,
            Email = email,
            Password = "OldPassword123!",
            ConfirmPassword = "OldPassword123!",
            FirstName = "Audit",
            LastName = "PwChange"
        };

        _ = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        User user = await context.Users.FirstAsync(u => u.Username == username);
        user.IsActive = true;
        _ = await context.SaveChangesAsync();

        // Act - Change password by calling the service directly (avoids controller model binding subtleties in tests)
        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        bool success = await authService.ChangePasswordAsync(user.Id, "OldPassword123!", "NewPassword123!");
        _ = success.Should().BeTrue();

        // Assert - Check audit log
        using IServiceScope verifyScope = _factory.Services.CreateScope();
        AppDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        DumpAuthAuditInfo(verifyContext, "PasswordChange");

        List<AuthAuditLog> auditLogs = await verifyContext.AuthAuditLogs
            .Where(a => a.UserId == user.Id && a.EventType == AuthEventType.PasswordChange)
            .ToListAsync();

        _ = auditLogs.Should().HaveCount(1);
        _ = auditLogs[0].Success.Should().BeTrue();
        _ = auditLogs[0].EventType.Should().Be(AuthEventType.PasswordChange);
    }

    [Fact]
    public async Task PasswordResetInitiated_CreatesAuditLogEntry()
    {
        // Arrange - Create user
        string email = $"auditreset{Guid.NewGuid():N}@test.com";

        RegisterRequest registerRequest = new RegisterRequest
        {
            Username = $"auditreset{Guid.NewGuid():N}",
            Email = email,
            Password = "SecurePassword123!",
            ConfirmPassword = "SecurePassword123!",
            FirstName = "Audit",
            LastName = "Reset"
        };

        _ = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        // Act - Initiate password reset
        ForgotPasswordRequest forgotPasswordRequest = new ForgotPasswordRequest
        {
            Email = email
        };
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/forgot-password", forgotPasswordRequest);

        // Assert - Check audit log
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        DumpAuthAuditInfo(context, "PasswordResetInitiated");

        // The reset email is masked before being persisted to the audit metadata
        // (see AuthAuditService.MaskIdentifier: "{local[0]}***{local[^1]}@{domain}").
        // Filter by the masked value so the assertion targets this test's own entry
        // and is not affected by other entries that share the audit log table.
        string localPart = email.Split('@')[0];
        string maskedEmail = $"{localPart[0]}***{localPart[^1]}@test.com";

        List<AuthAuditLog> auditLogs = await context.AuthAuditLogs
            .Where(a => a.EventType == AuthEventType.PasswordResetInitiated
                && a.Metadata != null && a.Metadata.Contains(maskedEmail))
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync();

        _ = auditLogs.Should().NotBeEmpty();
        AuthAuditLog resetLog = auditLogs.First();
        _ = resetLog.Success.Should().BeTrue();
        _ = resetLog.Metadata.Should().Contain(maskedEmail);
        _ = resetLog.Metadata.Should().NotContain(email, "raw emails must never be persisted to audit metadata");
    }

    [Fact]
    public async Task PasswordResetCompleted_CreatesAuditLogEntry()
    {
        // Arrange - Create user and reset token
        string username = $"auditresetcomp{Guid.NewGuid():N}";
        string email = $"{username}@test.com";

        RegisterRequest registerRequest = new RegisterRequest
        {
            Username = username,
            Email = email,
            Password = "OldPassword123!",
            ConfirmPassword = "OldPassword123!",
            FirstName = "Audit",
            LastName = "ResetComp"
        };

        _ = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashingService = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        User user = await context.Users.FirstAsync(u => u.Username == username);
        user.IsActive = true;

        // Create password reset token
        PasswordResetToken resetToken = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "test-reset-token-123",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            IsUsed = false
        };

        _ = context.PasswordResetTokens.Add(resetToken);
        _ = await context.SaveChangesAsync();

        // Act - Complete password reset
        ResetPasswordRequest resetPasswordRequest = new ResetPasswordRequest
        {
            Token = "test-reset-token-123",
            Email = email,
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        };

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/auth/reset-password", resetPasswordRequest);

        // Assert - Check audit log
        using IServiceScope verifyScope = _factory.Services.CreateScope();
        AppDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        DumpAuthAuditInfo(verifyContext, "PasswordResetCompleted");

        List<AuthAuditLog> auditLogs = await verifyContext.AuthAuditLogs
            .Where(a => a.UserId == user.Id && a.EventType == AuthEventType.PasswordReset)
            .ToListAsync();

        _ = auditLogs.Should().HaveCount(1);
        _ = auditLogs[0].Success.Should().BeTrue();
        _ = auditLogs[0].EventType.Should().Be(AuthEventType.PasswordReset);
    }

    [Fact]
    public async Task AccountLocked_CreatesAuditLogEntry()
    {
        // Arrange - Create user
        string username = $"auditlockuser{Guid.NewGuid():N}";
        string email = $"{username}@test.com";

        RegisterRequest registerRequest = new RegisterRequest
        {
            Username = username,
            Email = email,
            Password = "SecurePassword123!",
            ConfirmPassword = "SecurePassword123!",
            FirstName = "Audit",
            LastName = "Lock"
        };

        _ = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        User user = await context.Users.FirstAsync(u => u.Username == username);
        user.IsActive = true;
        _ = await context.SaveChangesAsync();

        // Act - Trigger account lockout with 5 failed login attempts
        for (int i = 0; i < 5; i++)
        {
            LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = username, Password = "WrongPassword!" };
            _ = await _client.PostAsJsonAsync("/api/auth/login", new { UsernameOrEmail = loginRequest.UsernameOrEmail, Password = loginRequest.Password });
        }

        // Assert - Check audit log for account locked event
        using IServiceScope verifyScope = _factory.Services.CreateScope();
        AppDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        DumpAuthAuditInfo(verifyContext, "AccountLocked");

        List<AuthAuditLog> auditLogs = await verifyContext.AuthAuditLogs
            .Where(a => a.UserId == user.Id && a.EventType == AuthEventType.AccountLocked)
            .ToListAsync();

        _ = auditLogs.Should().HaveCount(1);
        _ = auditLogs[0].Success.Should().BeTrue();
        _ = auditLogs[0].Metadata.Should().Contain("5"); // Should contain attempt count
        _ = auditLogs[0].Metadata.Should().Contain("LockoutDurationMinutes"); // Should contain lockout duration
    }

    [Fact]
    public async Task GetUserAuditLog_ReturnsUserEvents()
    {
        // Arrange - Create user and perform actions
        string username = $"auditgetlog{Guid.NewGuid():N}";
        string email = $"{username}@test.com";

        RegisterRequest registerRequest = new RegisterRequest
        {
            Username = username,
            Email = email,
            Password = "SecurePassword123!",
            ConfirmPassword = "SecurePassword123!",
            FirstName = "Audit",
            LastName = "GetLog"
        };

        _ = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = registerRequest.Username,
            Email = registerRequest.Email,
            Password = registerRequest.Password,
            ConfirmPassword = registerRequest.Password,
            FirstName = registerRequest.FirstName,
            LastName = registerRequest.LastName
        });

        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        User user = await context.Users.FirstAsync(u => u.Username == username);
        user.IsActive = true;
        _ = await context.SaveChangesAsync();

        // Perform login
        LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = username, Password = "SecurePassword123!" };
        _ = await _client.PostAsJsonAsync("/api/auth/login", new { UsernameOrEmail = loginRequest.UsernameOrEmail, Password = loginRequest.Password });

        // Act - Get audit log via service
        IAuthAuditService auditService = scope.ServiceProvider.GetRequiredService<IAuthAuditService>();
        List<AuthAuditLog> auditLogs = await auditService.GetUserAuditLogAsync(user.Id, pageSize: 10);

        DumpAuthAuditInfo(context, "GetUserAuditLog");

        // Assert
        _ = auditLogs.Should().HaveCountGreaterThanOrEqualTo(2); // Register + Login
        _ = auditLogs.Should().Contain(log => log.EventType == AuthEventType.Register);
        _ = auditLogs.Should().Contain(log => log.EventType == AuthEventType.Login);
        _ = auditLogs.Should().OnlyContain(log => log.UserId == user.Id);
    }
}
