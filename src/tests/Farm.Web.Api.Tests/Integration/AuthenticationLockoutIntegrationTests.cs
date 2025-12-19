using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using LoginRequest = Farm.Infrastructure.Contracts.Auth.LoginRequest;

namespace Farm.Web.Api.Tests.Integration;

public class AuthenticationLockoutIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthenticationLockoutIntegrationTests()
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

    [Fact]
    public async Task Login_LocksAccount_AfterMaxFailedAttempts()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        // Create test user
        User user = new User
        {
            Id = Guid.NewGuid(),
            Username = "lockouttest",
            Email = "lockout@test.com",
            PasswordHash = passwordHashing.HashPassword("ValidPassword123!"),
            IsActive = true,
            EmailConfirmed = true,
            FailedLoginAttempts = 0
        };
        _ = context.Users.Add(user);
        _ = await context.SaveChangesAsync();

        HttpClient client = _factory.CreateClient();

        // Act - Make 5 failed login attempts (default threshold)
        for (int i = 0; i < 5; i++)
        {
            LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = "lockouttest", Password = "WrongPassword" };
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", loginRequest);
            _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // Assert - Next login should be blocked due to lockout
        LoginRequest finalRequest = new LoginRequest { UsernameOrEmail = "lockouttest", Password = "ValidPassword123!" };
        HttpResponseMessage finalResponse = await client.PostAsJsonAsync("/api/auth/login", finalRequest);

        _ = finalResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        string errorContent = await finalResponse.Content.ReadAsStringAsync();
        _ = errorContent.Should().Contain("locked");
    }

    [Fact]
    public async Task Login_ResetsCounter_OnSuccessfulLogin()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        User user = new User
        {
            Id = Guid.NewGuid(),
            Username = "resettest",
            Email = "reset@test.com",
            PasswordHash = passwordHashing.HashPassword("ValidPassword123!"),
            IsActive = true,
            EmailConfirmed = true,
            FailedLoginAttempts = 3 // Already has some failed attempts
        };
        _ = context.Users.Add(user);
        _ = await context.SaveChangesAsync();

        HttpClient client = _factory.CreateClient();

        // Act - Successful login
        LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = "resettest", Password = "ValidPassword123!" };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify counter was reset (reload from a fresh scope to avoid caching)
        using IServiceScope verifyScope = _factory.Services.CreateScope();
        AppDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        User? updatedUser = await verifyContext.Users.FindAsync(user.Id);
        _ = updatedUser.Should().NotBeNull();
        _ = updatedUser!.FailedLoginAttempts.Should().Be(0);
        _ = updatedUser.LockoutEnd.Should().BeNull();
    }

    [Fact]
    public async Task Login_AllowsLogin_AfterLockoutExpires()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        User user = new User
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
        _ = context.Users.Add(user);
        _ = await context.SaveChangesAsync();

        HttpClient client = _factory.CreateClient();

        // Act - Login with valid credentials after lockout expired
        LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = "expiredtest", Password = "ValidPassword123!" };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert - Should succeed
        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        AuthenticationResult? authResult = await response.Content.ReadFromJsonAsync<AuthenticationResult>();
        _ = authResult.Should().NotBeNull();
        _ = authResult!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task Login_TracksFailedAttempts_ForNonExistentUsers()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        HttpClient client = _factory.CreateClient();

        // Act - Try to login with non-existent user
        LoginRequest loginRequest = new LoginRequest { UsernameOrEmail = "doesnotexist", Password = "SomePassword123!" };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", loginRequest);

        // Assert
        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Verify audit entry was created
        List<FailedLoginAttempt> auditEntries = context.FailedLoginAttempts
            .Where(f => f.Identifier == "doesnotexist")
            .ToList();
        _ = auditEntries.Should().HaveCountGreaterThan(0);
    }
}
