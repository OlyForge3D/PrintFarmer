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
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        User user = new User
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

        HttpClient client = _factory.CreateClient();

        // Act
        ForgotPasswordRequest request = new ForgotPasswordRequest { Email = "reset@test.com" };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/forgot-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ForgotPasswordResponse? result = await response.Content.ReadFromJsonAsync<ForgotPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();

        // Verify token was created in database
        using IServiceScope verifyScope = _factory.Services.CreateScope();
        AppDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<PasswordResetToken> tokens = await verifyContext.PasswordResetTokens
            .Where(t => t.UserId == user.Id && !t.IsUsed)
            .ToListAsync();
        tokens.Should().HaveCountGreaterThan(0);
        tokens[0].ExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task ForgotPassword_ReturnsSuccess_ForNonExistentEmail()
    {
        // Arrange
        HttpClient client = _factory.CreateClient();

        // Act - Request reset for non-existent email
        ForgotPasswordRequest request = new ForgotPasswordRequest { Email = "nonexistent@test.com" };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/forgot-password", request);

        // Assert - Should return success to prevent email enumeration
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ForgotPasswordResponse? result = await response.Content.ReadFromJsonAsync<ForgotPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.Message.Should().Contain("If an account with that email exists");
    }

    [Fact]
    public async Task ResetPassword_SucceedsWithValidToken()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        User user = new User
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
        PasswordResetToken resetToken = new PasswordResetToken
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

        HttpClient client = _factory.CreateClient();

        // Act
        ResetPasswordRequest request = new ResetPasswordRequest
        {
            Token = "valid-reset-token-12345",
            Email = "resetvalid@test.com",
            NewPassword = "NewSecurePassword123!",
            ConfirmPassword = "NewSecurePassword123!"
        };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/reset-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ResetPasswordResponse? result = await response.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();

        // Verify password was changed
        using IServiceScope verifyScope = _factory.Services.CreateScope();
        AppDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        User? updatedUser = await verifyContext.Users.FindAsync(user.Id);
        updatedUser.Should().NotBeNull();

        // Verify old password no longer works
        IPasswordHashingService verifyHashing = verifyScope.ServiceProvider.GetRequiredService<IPasswordHashingService>();
        verifyHashing.VerifyPassword("OldPassword123!", updatedUser!.PasswordHash).Should().BeFalse();

        // Verify new password works
        verifyHashing.VerifyPassword("NewSecurePassword123!", updatedUser.PasswordHash).Should().BeTrue();

        // Verify token is marked as used
        PasswordResetToken? usedToken = await verifyContext.PasswordResetTokens.FindAsync(resetToken.Id);
        usedToken.Should().NotBeNull();
        usedToken!.IsUsed.Should().BeTrue();
        usedToken.UsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ResetPassword_FailsWithExpiredToken()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        User user = new User
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
        PasswordResetToken resetToken = new PasswordResetToken
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

        HttpClient client = _factory.CreateClient();

        // Act
        ResetPasswordRequest request = new ResetPasswordRequest
        {
            Token = "expired-token-12345",
            Email = "expired@test.com",
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/reset-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ResetPasswordResponse? result = await response.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Message.Should().Contain("Invalid or expired");
    }

    [Fact]
    public async Task ResetPassword_FailsWithUsedToken()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        User user = new User
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
        PasswordResetToken resetToken = new PasswordResetToken
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

        HttpClient client = _factory.CreateClient();

        // Act
        ResetPasswordRequest request = new ResetPasswordRequest
        {
            Token = "used-token-12345",
            Email = "used@test.com",
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/reset-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ResetPasswordResponse? result = await response.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ResetPassword_FailsWithInvalidToken()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        User user = new User
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

        HttpClient client = _factory.CreateClient();

        // Act - Try with non-existent token
        ResetPasswordRequest request = new ResetPasswordRequest
        {
            Token = "nonexistent-token",
            Email = "invalid@test.com",
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/reset-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ResetPasswordResponse? result = await response.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ResetPassword_FailsWithMismatchedEmail()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        User user = new User
        {
            Id = Guid.NewGuid(),
            Username = "mismatchuser",
            Email = "correct@test.com",
            PasswordHash = passwordHashing.HashPassword("OldPassword123!"),
            IsActive = true,
            EmailConfirmed = true
        };
        context.Users.Add(user);

        PasswordResetToken resetToken = new PasswordResetToken
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

        HttpClient client = _factory.CreateClient();

        // Act - Try with wrong email
        ResetPasswordRequest request = new ResetPasswordRequest
        {
            Token = "valid-token-12345",
            Email = "wrong@test.com", // Different email
            NewPassword = "NewPassword123!",
            ConfirmPassword = "NewPassword123!"
        };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/reset-password", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ResetPasswordResponse? result = await response.Content.ReadFromJsonAsync<ResetPasswordResponse>();
        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ForgotPassword_RespectsRateLimiting()
    {
        // Arrange
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IPasswordHashingService passwordHashing = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

        User user = new User
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

        HttpClient client = _factory.CreateClient();

        // Act - Make multiple rapid requests (default limit is 3 per hour from config)
        ForgotPasswordRequest request = new ForgotPasswordRequest { Email = "ratelimit@test.com" };

        for (int i = 0; i < 3; i++)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/forgot-password", request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // 4th request should still return success (to prevent information leakage)
        // but internally should be rate limited
        HttpResponseMessage finalResponse = await client.PostAsJsonAsync("/api/auth/forgot-password", request);
        finalResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify that no new tokens are created beyond rate limit
        // (This is a simplified check - actual behavior depends on rate limit configuration)
        using IServiceScope verifyScope = _factory.Services.CreateScope();
        AppDbContext verifyContext = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        int tokenCount = await verifyContext.PasswordResetTokens
            .Where(t => t.UserId == user.Id)
            .CountAsync();

        // Should have created tokens for the first 3 requests only
        tokenCount.Should().BeLessOrEqualTo(3);
    }
}
