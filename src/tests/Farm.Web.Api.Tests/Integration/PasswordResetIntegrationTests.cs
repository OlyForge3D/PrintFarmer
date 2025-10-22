using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.Authentication;
using Farm.Web.Shared.Contracts.Auth;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

public class PasswordResetIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PasswordResetIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ForgotPassword_CreatesToken_ForValidEmail()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "resetuser",
            Email = "reset@test.com",
            PasswordHash = passwordHashing.HashPassword("OldPassword123!"),
            IsActive = true,
            EmailConfirmed = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var client = _factory.CreateClient();

        // Act
        var request = new ForgotPasswordRequest { Email = "reset@test.com" };
        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ForgotPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();

        // Verify token was created in database
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokens = await verifyContext.PasswordResetTokens
            .Where(t => t.UserId == user.Id && !t.IsUsed)
            .ToListAsync();
        tokens.Should().HaveCountGreaterThan(0);
        tokens[0].ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task ForgotPassword_ReturnsSuccess_ForNonExistentEmail()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act - Request reset for non-existent email
        var request = new ForgotPasswordRequest { Email = "nonexistent@test.com" };
        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", request);

        // Assert - Should return success to prevent email enumeration
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ForgotPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Message.Should().Contain("If an account with that email exists");
    }

    [Fact]
    public async Task ResetPassword_SucceedsWithValidToken()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "resetvaliduser",
            Email = "resetvalid@test.com",
            PasswordHash = passwordHashing.HashPassword("OldPassword123!"),
            IsActive = true,
            EmailConfirmed = true
        };
        context.Users.Add(user);

        // Create a valid reset token
        var resetToken = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "valid-reset-token-12345",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            IsUsed = false
        };
        context.PasswordResetTokens.Add(resetToken);
        await context.SaveChangesAsync();

        var client = _factory.CreateClient();

        // Act
        var request = new ResetPasswordRequest
        {
            Token = "valid-reset-token-12345",
            Email = "resetvalid@test.com",
            NewPassword = "NewSecurePassword123!",
            ConfirmPassword = "NewSecurePassword123!"
        };
        var response = await client.PostAsJsonAsync("/api/auth/reset-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();

        // Verify password was changed
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var updatedUser = await verifyContext.Users.FindAsync(user.Id);
        updatedUser.Should().NotBeNull();

        // Verify old password no longer works
        var verifyHashing = verifyScope.ServiceProvider.GetRequiredService<IPasswordHashingService>();
        verifyHashing.VerifyPassword("OldPassword123!", updatedUser!.PasswordHash).Should().BeFalse();

        // Verify new password works
        verifyHashing.VerifyPassword("NewSecurePassword123!", updatedUser.PasswordHash).Should().BeTrue();

        // Verify token is marked as used
        var usedToken = await verifyContext.PasswordResetTokens.FindAsync(resetToken.Id);
        usedToken.Should().NotBeNull();
        usedToken!.IsUsed.Should().BeTrue();
        usedToken.UsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ResetPassword_FailsWithExpiredToken()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "expiredtokenuser",
            Email = "expired@test.com",
            PasswordHash = passwordHashing.HashPassword("OldPassword123!"),
            IsActive = true,
            EmailConfirmed = true
        };
        context.Users.Add(user);

        // Create an expired reset token
        var resetToken = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "expired-token-12345",
            CreatedAt = DateTime.UtcNow.AddHours(-2),
            ExpiresAt = DateTime.UtcNow.AddHours(-1), // Expired 1 hour ago
            IsUsed = false
        };
        context.PasswordResetTokens.Add(resetToken);
        await context.SaveChangesAsync();

        var client = _factory.CreateClient();

        // Act
        var request = new ResetPasswordRequest
        {
            Token = "expired-token-12345",
            Email = "expired@test.com",
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        };
        var response = await client.PostAsJsonAsync("/api/auth/reset-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Message.Should().Contain("Invalid or expired");
    }

    [Fact]
    public async Task ResetPassword_FailsWithUsedToken()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "usedtokenuser",
            Email = "used@test.com",
            PasswordHash = passwordHashing.HashPassword("OldPassword123!"),
            IsActive = true,
            EmailConfirmed = true
        };
        context.Users.Add(user);

        // Create a used reset token
        var resetToken = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "used-token-12345",
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
            IsUsed = true, // Already used
            UsedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        context.PasswordResetTokens.Add(resetToken);
        await context.SaveChangesAsync();

        var client = _factory.CreateClient();

        // Act
        var request = new ResetPasswordRequest
        {
            Token = "used-token-12345",
            Email = "used@test.com",
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        };
        var response = await client.PostAsJsonAsync("/api/auth/reset-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ResetPassword_FailsWithInvalidToken()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "invalidtokenuser",
            Email = "invalid@test.com",
            PasswordHash = passwordHashing.HashPassword("OldPassword123!"),
            IsActive = true,
            EmailConfirmed = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var client = _factory.CreateClient();

        // Act - Try with non-existent token
        var request = new ResetPasswordRequest
        {
            Token = "nonexistent-token",
            Email = "invalid@test.com",
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        };
        var response = await client.PostAsJsonAsync("/api/auth/reset-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ResetPassword_FailsWithMismatchedEmail()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "mismatchuser",
            Email = "correct@test.com",
            PasswordHash = passwordHashing.HashPassword("OldPassword123!"),
            IsActive = true,
            EmailConfirmed = true
        };
        context.Users.Add(user);

        var resetToken = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = "valid-token-12345",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            IsUsed = false
        };
        context.PasswordResetTokens.Add(resetToken);
        await context.SaveChangesAsync();

        var client = _factory.CreateClient();

        // Act - Try with wrong email
        var request = new ResetPasswordRequest
        {
            Token = "valid-token-12345",
            Email = "wrong@test.com", // Different email
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        };
        var response = await client.PostAsJsonAsync("/api/auth/reset-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ForgotPassword_RespectsRateLimiting()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "ratelimituser",
            Email = "ratelimit@test.com",
            PasswordHash = passwordHashing.HashPassword("Password123!"),
            IsActive = true,
            EmailConfirmed = true
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var client = _factory.CreateClient();

        // Act - Make multiple rapid requests (default limit is 3 per hour from config)
        var request = new ForgotPasswordRequest { Email = "ratelimit@test.com" };

        for (int i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/forgot-password", request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // 4th request should still return success (to prevent information leakage)
        // but internally should be rate limited
        var finalResponse = await client.PostAsJsonAsync("/api/auth/forgot-password", request);
        finalResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify that no new tokens are created beyond rate limit
        // (This is a simplified check - actual behavior depends on rate limit configuration)
        using var verifyScope = _factory.Services.CreateScope();
        var verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenCount = await verifyContext.PasswordResetTokens
            .Where(t => t.UserId == user.Id)
            .CountAsync();

        // Should have created tokens for the first 3 requests only
        tokenCount.Should().BeLessOrEqualTo(3);
    }
}
