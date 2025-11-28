using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.IntegrationTests;
using Farm.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using LoginRequest = Farm.Infrastructure.Contracts.Auth.LoginRequest;
using RegisterRequest = Farm.Infrastructure.Contracts.Auth.RegisterRequest;

namespace Farm.Web.Api.Tests.Authentication;

[Collection("Sequential")]
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
        HttpClient client = CreateClient();

        // Create and login as admin
        (string? adminToken, Guid adminUserId) = await CreateAdminUserAsync();

        // Create a regular user
        (string? userToken, Guid userId) = await CreateRegularUserAsync("testuser", "test@example.com", "TestPassword123!");

        // Verify user can access protected endpoint with their token
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        HttpResponseMessage beforeRevocationResponse = await client.GetAsync("/api/users");
        _ = beforeRevocationResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden); // Regular user can't access admin endpoint, but token is valid

        // Act - Admin revokes all user sessions
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var revokeRequest = new { Reason = "Security test - revoking sessions" };
        HttpResponseMessage revokeResponse = await client.PostAsJsonAsync($"/api/users/{userId}/revoke-sessions", revokeRequest);

        // Assert - Revocation succeeds
        _ = revokeResponse.IsSuccessStatusCode.Should().BeTrue();
        RevokeSessionsResult? revokeResult = await revokeResponse.Content.ReadFromJsonAsync<RevokeSessionsResult>();
        _ = revokeResult.Should().NotBeNull();
        _ = revokeResult!.UserId.Should().Be(userId);
        _ = revokeResult.RevokedCount.Should().BeGreaterThan(0);

        // User's token should now be invalid
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        HttpResponseMessage afterRevocationResponse = await client.GetAsync("/api/users");
        _ = afterRevocationResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "revoked token should be rejected");
    }

    [Fact]
    public async Task RevokeAllUserSessions_AdminCannotRevokeSelf_ShouldReturn400()
    {
        // Arrange
        HttpClient client = CreateClient();
        (string? adminToken, Guid adminUserId) = await CreateAdminUserAsync();

        // Act - Admin tries to revoke their own sessions
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var revokeRequest = new { Reason = "Trying to revoke self" };
        HttpResponseMessage response = await client.PostAsJsonAsync($"/api/users/{adminUserId}/revoke-sessions", revokeRequest);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        string content = await response.Content.ReadAsStringAsync();
        _ = content.Should().Contain("cannot revoke their own sessions");
    }

    [Fact]
    public async Task RevokeAllUserSessions_NonExistentUser_ShouldReturn404()
    {
        // Arrange
        HttpClient client = CreateClient();
        (string? adminToken, _) = await CreateAdminUserAsync();
        Guid nonExistentUserId = Guid.NewGuid();

        // Act
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var revokeRequest = new { Reason = "Test" };
        HttpResponseMessage response = await client.PostAsJsonAsync($"/api/users/{nonExistentUserId}/revoke-sessions", revokeRequest);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetRevokedTokens_ShouldReturnRevocationHistory()
    {
        // Arrange
        HttpClient client = CreateClient();
        (string? adminToken, Guid adminUserId) = await CreateAdminUserAsync();
        (string? userToken, Guid userId) = await CreateRegularUserAsync("testuser2", "test2@example.com", "TestPassword123!");

        // Revoke user sessions
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var revokeRequest = new { Reason = "Test revocation for history" };
        _ = await client.PostAsJsonAsync($"/api/users/{userId}/revoke-sessions", revokeRequest);

        // Act - Get revocation history
        HttpResponseMessage response = await client.GetAsync($"/api/users/{userId}/revoked-tokens");

        // Assert
        _ = response.IsSuccessStatusCode.Should().BeTrue();
        List<RevokedTokenDto> revocations = (await response.Content.ReadFromJsonAsync<List<RevokedTokenDto>>()) ?? new List<RevokedTokenDto>();
        _ = revocations.Should().NotBeEmpty();
        // Guarded use of FirstOrDefault() with explicit NotBeNull assertion to satisfy static analysis
        RevokedTokenDto? first = revocations.FirstOrDefault();
        _ = first.Should().NotBeNull();
        _ = first!.Reason.Should().Contain("Test revocation for history");
        _ = first.RevokedByUserId.Should().Be(adminUserId);
    }

    [Fact]
    public async Task CleanupExpiredRevocations_ShouldRemoveOldRecords()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid userId = Guid.NewGuid();

        // Ensure the user exists so RevokedToken FK constraints are satisfied
        User? existingUser = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (existingUser == null)
        {
            _ = db.Users.Add(new User
            {
                Id = userId,
                Username = $"revocation_{userId}",
                Email = $"revocation_{userId}@example.com",
                IsActive = true,
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password123!")
            });
            _ = await db.SaveChangesAsync();
        }

        // Create an expired revocation record (already past expiration)
        RevokedToken expiredRevocation = new RevokedToken
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
        RevokedToken activeRevocation = new RevokedToken
        {
            Id = Guid.NewGuid(),
            TokenHash = "active_token_hash_12345678901234567890123456789012",
            UserId = userId,
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7), // Still valid
            Reason = "Test active revocation",
            IpAddress = "127.0.0.1"
        };

        _ = db.RevokedTokens.Add(expiredRevocation);
        _ = db.RevokedTokens.Add(activeRevocation);
        _ = await db.SaveChangesAsync();

        // Act - Run cleanup
        ITokenRevocationService tokenRevocationService = scope.ServiceProvider.GetRequiredService<ITokenRevocationService>();
        int deletedCount = await tokenRevocationService.CleanupExpiredRevocationsAsync();

        // Assert
        _ = deletedCount.Should().BeGreaterThan(0, "at least the expired record should be deleted");

        // Verify expired record is gone
        bool expiredExists = await db.RevokedTokens.AnyAsync(r => r.Id == expiredRevocation.Id);
        _ = expiredExists.Should().BeFalse("expired revocation should be deleted");

        // Verify active record still exists
        bool activeExists = await db.RevokedTokens.AnyAsync(r => r.Id == activeRevocation.Id);
        _ = activeExists.Should().BeTrue("active revocation should remain");
    }

    [Fact]
    public async Task TokenRevocation_AfterMultipleSessions_ShouldRevokeAll()
    {
        // Arrange - Create user and get multiple tokens (simulating multiple devices)
        (string? token1, Guid userId) = await CreateRegularUserAsync("multidevice", "multi@example.com", "Password123!");

        // Login again to get a second token (different session)
        HttpClient client = CreateClient();
        LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = "multidevice", Password = "Password123!" };
        HttpResponseMessage loginResponse = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
        AuthenticationResult? loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();
        string token2 = loginResult!.Token!;

        // Both tokens should work initially
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token1);
        HttpResponseMessage response1Before = await client.GetAsync("/healthz");
        _ = response1Before.IsSuccessStatusCode.Should().BeTrue();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token2);
        HttpResponseMessage response2Before = await client.GetAsync("/healthz");
        _ = response2Before.IsSuccessStatusCode.Should().BeTrue();

        // Act - Admin revokes all sessions
        (string? adminToken, _) = await CreateAdminUserAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var revokeRequest = new { Reason = "Revoking all sessions for security" };
        _ = await client.PostAsJsonAsync($"/api/users/{userId}/revoke-sessions", revokeRequest);

        // Assert - Both tokens should now be invalid
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token1);
        HttpResponseMessage response1After = await client.GetAsync("/api/users");
        _ = response1After.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token2);
        HttpResponseMessage response2After = await client.GetAsync("/api/users");
        _ = response2After.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<(string Token, Guid UserId)> CreateRegularUserAsync(string username, string email, string password)
    {
        HttpClient client = CreateClient();

        // Register user
        RegisterRequest registerRequest = new RegisterRequest { Username = username, Email = email, Password = password, ConfirmPassword = password, FirstName = "Test", LastName = "User" };
        HttpResponseMessage registerResponse = await client.PostAsJsonAsync("/api/auth/register", registerRequest);

        // If user already exists or needs approval, try to login
        if (!registerResponse.IsSuccessStatusCode)
        {
            // User might already exist, try login
            LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = username, Password = password };
            HttpResponseMessage loginResponse = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
            AuthenticationResult? loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();
            return (loginResult!.Token!, loginResult.User!.Id);
        }

        AuthenticationResult? registerResult = await registerResponse.Content.ReadFromJsonAsync<AuthenticationResult>();

        // If user needs approval, activate them via database
        if (registerResult!.Token == null)
        {
            using IServiceScope scope = _factory.Services.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User? user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user != null)
            {
                user.IsActive = true;
                _ = await db.SaveChangesAsync();
            }

            // Login after activation
            LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = username, Password = password };
            HttpResponseMessage loginResponse = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
            AuthenticationResult? loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();
            return (loginResult!.Token!, loginResult.User!.Id);
        }

        return (registerResult.Token!, registerResult.User!.Id);
    }

    private async Task<(string Token, Guid UserId)> CreateAdminUserAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();

        // Check if admin already exists
        User? existingAdmin = await db.Users.FirstOrDefaultAsync(u => u.Username == "testadmin");
        if (existingAdmin != null)
        {
            // Login as existing admin
            HttpClient client = CreateClient();
            LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = "testadmin", Password = "AdminPassword123!" };
            HttpResponseMessage loginResponse = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
            AuthenticationResult? loginResult = await loginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();
            return (loginResult!.Token!, loginResult.User!.Id);
        }

        // Create new admin user
        User adminUser = new User
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

        string passwordHash = BCrypt.Net.BCrypt.HashPassword("AdminPassword123!");
        adminUser.PasswordHash = passwordHash;

        _ = db.Users.Add(adminUser);

        // Add admin role
        Role? adminRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == "farm_admin");
        if (adminRole == null)
        {
            adminRole = new Role { Id = Guid.NewGuid(), Name = "farm_admin", CreatedAt = DateTime.UtcNow };
            _ = db.Roles.Add(adminRole);
        }

        UserRole userRole = new UserRole { UserId = adminUser.Id, RoleId = adminRole.Id };
        _ = db.UserRoles.Add(userRole);

        _ = await db.SaveChangesAsync();

        // Get token via login
        HttpClient adminClient = CreateClient();
        LoginRequest adminLoginRequest = new LoginRequest { UsernameOrEmail = "testadmin", Password = "AdminPassword123!" };
        HttpResponseMessage adminLoginResponse = await adminClient.PostAsJsonAsync("/api/auth/login", adminLoginRequest);
        AuthenticationResult? adminLoginResult = await adminLoginResponse.Content.ReadFromJsonAsync<AuthenticationResult>();

        return (adminLoginResult!.Token!, adminUser.Id);
    }

    private record RevokeSessionsResult(Guid UserId, int RevokedCount, DateTime RevokedAt);
    private record RevokedTokenDto(Guid Id, DateTime RevokedAt, string Reason, DateTime ExpiresAt, string? IpAddress, Guid? RevokedByUserId);
}
