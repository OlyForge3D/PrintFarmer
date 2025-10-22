using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.IntegrationTests;
using Farm.Web.Shared;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using LoginRequest = Farm.Web.Shared.Contracts.Auth.LoginRequest;
using RegisterRequest = Farm.Web.Shared.Contracts.Auth.RegisterRequest;

namespace Farm.Web.Api.Tests.Authentication;

public class SessionRevocationIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SessionRevocationIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    [Fact]
    public async Task RevokeAllUserSessions_ShouldInvalidateTokens()
    {
        // Arrange
        var client = CreateClient();
        
        // Create and login as admin
        var (adminToken, adminUserId) = await CreateAdminUserAsync();
        
        // Create a regular user
        var (userToken, userId) = await CreateRegularUserAsync("testuser", "test@example.com", "TestPassword123!");
        
        // Verify user can access protected endpoint with their token
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        var beforeRevocationResponse = await client.GetAsync("/api/users");
        beforeRevocationResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden); // Regular user can't access admin endpoint, but token is valid

        // Act - Admin revokes all user sessions
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var revokeRequest = new { Reason = "Security test - revoking sessions" };
        var revokeResponse = await client.PostAsJsonAsync($"/api/users/{userId}/revoke-sessions", revokeRequest);

        // Assert - Revocation succeeds
        revokeResponse.IsSuccessStatusCode.Should().BeTrue();
        var revokeResult = await revokeResponse.Content.ReadFromJsonAsync<RevokeSessionsResult>();
        revokeResult.Should().NotBeNull();
        revokeResult!.UserId.Should().Be(userId);
        revokeResult.RevokedCount.Should().BeGreaterThan(0);

        // User's token should now be invalid
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        var afterRevocationResponse = await client.GetAsync("/api/users");
        afterRevocationResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "revoked token should be rejected");
    }

    [Fact]
    public async Task RevokeAllUserSessions_AdminCannotRevokeSelf_ShouldReturn400()
    {
        // Arrange
        var client = CreateClient();
        var (adminToken, adminUserId) = await CreateAdminUserAsync();

        // Act - Admin tries to revoke their own sessions
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var revokeRequest = new { Reason = "Trying to revoke self" };
        var response = await client.PostAsJsonAsync($"/api/users/{adminUserId}/revoke-sessions", revokeRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("cannot revoke their own sessions");
    }

    [Fact]
    public async Task RevokeAllUserSessions_NonExistentUser_ShouldReturn404()
    {
        // Arrange
        var client = CreateClient();
        var (adminToken, _) = await CreateAdminUserAsync();
        var nonExistentUserId = Guid.NewGuid();

        // Act
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var revokeRequest = new { Reason = "Test" };
        var response = await client.PostAsJsonAsync($"/api/users/{nonExistentUserId}/revoke-sessions", revokeRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetRevokedTokens_ShouldReturnRevocationHistory()
    {
        // Arrange
        var client = CreateClient();
        var (adminToken, adminUserId) = await CreateAdminUserAsync();
        var (userToken, userId) = await CreateRegularUserAsync("testuser2", "test2@example.com", "TestPassword123!");

        // Revoke user sessions
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var revokeRequest = new { Reason = "Test revocation for history" };
        await client.PostAsJsonAsync($"/api/users/{userId}/revoke-sessions", revokeRequest);

        // Act - Get revocation history
        var response = await client.GetAsync($"/api/users/{userId}/revoked-tokens");

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
        var revocations = await response.Content.ReadFromJsonAsync<List<RevokedTokenDto>>();
        revocations.Should().NotBeNull();
        revocations!.Should().NotBeEmpty();
        revocations.First().Reason.Should().Contain("Test revocation for history");
        revocations.First().RevokedByUserId.Should().Be(adminUserId);
    }

    [Fact]
    public async Task CleanupExpiredRevocations_ShouldRemoveOldRecords()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = Guid.NewGuid();

        // Create an expired revocation record (already past expiration)
        var expiredRevocation = new RevokedToken
        {
            Id = Guid.NewGuid(),
            TokenHash = "expired_token_hash_12345678901234567890123456789012",
            UserId = userId,
            RevokedAt = DateTime.UtcNow.AddDays(-10),
            ExpiresAt = DateTime.UtcNow.AddDays(-1), // Already expired
            Reason = "Test expired revocation",
            IpAddress = "127.0.0.1"
        };

        // Create a non-expired revocation record
        var activeRevocation = new RevokedToken
        {
            Id = Guid.NewGuid(),
            TokenHash = "active_token_hash_12345678901234567890123456789012",
            UserId = userId,
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7), // Still valid
            Reason = "Test active revocation",
            IpAddress = "127.0.0.1"
        };

        db.RevokedTokens.Add(expiredRevocation);
        db.RevokedTokens.Add(activeRevocation);
        await db.SaveChangesAsync();

        // Act - Run cleanup
        var tokenRevocationService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Authentication.ITokenRevocationService>();
        var deletedCount = await tokenRevocationService.CleanupExpiredRevocationsAsync();

        // Assert
        deletedCount.Should().BeGreaterThan(0, "at least the expired record should be deleted");

        // Verify expired record is gone
        var expiredExists = await db.RevokedTokens.AnyAsync(r => r.Id == expiredRevocation.Id);
        expiredExists.Should().BeFalse("expired revocation should be deleted");

        // Verify active record still exists
        var activeExists = await db.RevokedTokens.AnyAsync(r => r.Id == activeRevocation.Id);
        activeExists.Should().BeTrue("active revocation should remain");
    }

    [Fact]
    public async Task TokenRevocation_AfterMultipleSessions_ShouldRevokeAll()
    {
        // Arrange - Create user and get multiple tokens (simulating multiple devices)
        var (token1, userId) = await CreateRegularUserAsync("multidevice", "multi@example.com", "Password123!");
        
        // Login again to get a second token (different session)
        var client = CreateClient();
        var loginRequest = new LoginRequest { UsernameOrEmail = "multidevice", Password = "Password123!" };
        var loginResponse = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();
        var token2 = loginResult!.Token!;

        // Both tokens should work initially
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token1);
        var response1Before = await client.GetAsync("/healthz");
        response1Before.IsSuccessStatusCode.Should().BeTrue();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token2);
        var response2Before = await client.GetAsync("/healthz");
        response2Before.IsSuccessStatusCode.Should().BeTrue();

        // Act - Admin revokes all sessions
        var (adminToken, _) = await CreateAdminUserAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var revokeRequest = new { Reason = "Revoking all sessions for security" };
        await client.PostAsJsonAsync($"/api/users/{userId}/revoke-sessions", revokeRequest);

        // Assert - Both tokens should now be invalid
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token1);
        var response1After = await client.GetAsync("/api/users");
        response1After.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token2);
        var response2After = await client.GetAsync("/api/users");
        response2After.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<(string Token, Guid UserId)> CreateRegularUserAsync(string username, string email, string password)
    {
        var client = CreateClient();
        
        // Register user
        var registerRequest = new RegisterRequest { Username = username, Email = email, Password = password, ConfirmPassword = password, FirstName = "Test", LastName = "User" };
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", registerRequest);
        
        // If user already exists or needs approval, try to login
        if (!registerResponse.IsSuccessStatusCode)
        {
            // User might already exist, try login
            var loginRequest = new LoginRequest { UsernameOrEmail = username, Password = password };
            var loginResponse = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
            var loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();
            return (loginResult!.Token!, loginResult.User!.Id);
        }

        var registerResult = await registerResponse.Content.ReadFromJsonAsync<AuthenticationResult>();
        
        // If user needs approval, activate them via database
        if (registerResult!.Token == null)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user != null)
            {
                user.IsActive = true;
                await db.SaveChangesAsync();
            }
            
            // Login after activation
            var loginRequest = new LoginRequest { UsernameOrEmail = username, Password = password };
            var loginResponse = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
            var loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();
            return (loginResult!.Token!, loginResult.User!.Id);
        }

        return (registerResult.Token!, registerResult.User!.Id);
    }

    private async Task<(string Token, Guid UserId)> CreateAdminUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var authService = scope.ServiceProvider.GetRequiredService<Farm.Web.Api.Services.Authentication.IAuthenticationService>();

        // Check if admin already exists
        var existingAdmin = await db.Users.FirstOrDefaultAsync(u => u.Username == "testadmin");
        if (existingAdmin != null)
        {
            // Login as existing admin
            var client = CreateClient();
            var loginRequest = new LoginRequest { UsernameOrEmail = "testadmin", Password = "AdminPassword123!" };
            var loginResponse = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
            var loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();
            return (loginResult!.Token!, loginResult.User!.Id);
        }

        // Create new admin user
        var adminUser = new User
        {
            Id = Guid.NewGuid(),
            Username = "testadmin",
            Email = "admin@example.com",
            FirstName = "Test",
            LastName = "Admin",
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow
        };

        var passwordHash = BCrypt.Net.BCrypt.HashPassword("AdminPassword123!");
        adminUser.PasswordHash = passwordHash;

        db.Users.Add(adminUser);

        // Add admin role
        var adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
        if (adminRole == null)
        {
            adminRole = new Role { Id = Guid.NewGuid(), Name = "farm_admin", CreatedAt = DateTime.UtcNow };
            db.Roles.Add(adminRole);
        }

        var userRole = new UserRole { UserId = adminUser.Id, RoleId = adminRole.Id };
        db.UserRoles.Add(userRole);

        await db.SaveChangesAsync();

        // Get token via login
        var adminClient = CreateClient();
        var adminLoginRequest = new LoginRequest { UsernameOrEmail = "testadmin", Password = "AdminPassword123!" };
        var adminLoginResponse = await adminClient.PostAsJsonAsync("/api/auth/login", adminLoginRequest);
        var adminLoginResult = await adminLoginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();

        return (adminLoginResult!.Token!, adminUser.Id);
    }

    private record RevokeSessionsResult(Guid UserId, int RevokedCount, DateTime RevokedAt);
    private record RevokedTokenDto(Guid Id, DateTime RevokedAt, string Reason, DateTime ExpiresAt, string? IpAddress, Guid? RevokedByUserId);
}
