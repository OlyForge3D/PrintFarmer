using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using LoginRequest = Farm.Web.Shared.Contracts.Auth.LoginRequest;

namespace Farm.Web.Api.Tests.Integration;

public class AuthenticationLockoutIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthenticationLockoutIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_LocksAccount_AfterMaxFailedAttempts()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        // Create test user
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "lockouttest",
            Email = "lockout@test.com",
            PasswordHash = passwordHashing.HashPassword("ValidPassword123!"),
            IsActive = true,
            EmailConfirmed = true,
            FailedLoginAttempts = 0
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var client = _factory.CreateClient();

        // Act - Make 5 failed login attempts (default threshold)
        for (int i = 0; i < 5; i++)
        {
            var loginRequest = new LoginRequest { UsernameOrEmail = "lockouttest", Password = "WrongPassword" };
            var response = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // Assert - Next login should be blocked due to lockout
        var finalRequest = new LoginRequest { UsernameOrEmail = "lockouttest", Password = "ValidPassword123!" };
        var finalResponse = await client.PostAsJsonAsync("/api/auth/login", finalRequest);
        
        finalResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var errorContent = await finalResponse.Content.ReadAsStringAsync();
        errorContent.Should().Contain("locked");
    }

    [Fact]
    public async Task Login_ResetsCounter_OnSuccessfulLogin()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "resettest",
            Email = "reset@test.com",
            PasswordHash = passwordHashing.HashPassword("ValidPassword123!"),
            IsActive = true,
            EmailConfirmed = true,
            FailedLoginAttempts = 3 // Already has some failed attempts
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var client = _factory.CreateClient();

        // Act - Successful login
        var loginRequest = new LoginRequest { UsernameOrEmail = "resettest", Password = "ValidPassword123!" };
        var response = await client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify counter was reset (reload from a fresh scope to avoid caching)
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var updatedUser = await verifyContext.Users.FindAsync(user.Id);
        updatedUser.Should().NotBeNull();
        updatedUser!.FailedLoginAttempts.Should().Be(0);
        updatedUser.LockoutEnd.Should().BeNull();
    }

    [Fact]
    public async Task Login_AllowsLogin_AfterLockoutExpires()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "expiredtest",
            Email = "expired@test.com",
            PasswordHash = passwordHashing.HashPassword("ValidPassword123!"),
            IsActive = true,
            EmailConfirmed = true,
            FailedLoginAttempts = 5,
            LockoutEnd = DateTime.UtcNow.AddSeconds(-10) // Expired 10 seconds ago
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var client = _factory.CreateClient();

        // Act - Login with valid credentials after lockout expired
        var loginRequest = new LoginRequest { UsernameOrEmail = "expiredtest", Password = "ValidPassword123!" };
        var response = await client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert - Should succeed
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var authResult = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        authResult.Should().NotBeNull();
        authResult!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Login_TracksFailedAttempts_ForNonExistentUsers()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = _factory.CreateClient();

        // Act - Try to login with non-existent user
        var loginRequest = new LoginRequest { UsernameOrEmail = "doesnotexist", Password = "SomePassword123!" };
        var response = await client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Verify audit entry was created
        var auditEntries = context.FailedLoginAttempts
            .Where(f => f.Identifier == "doesnotexist")
            .ToList();
        auditEntries.Should().HaveCountGreaterThan(0);
    }
}
