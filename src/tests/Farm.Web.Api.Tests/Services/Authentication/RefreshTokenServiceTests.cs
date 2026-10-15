using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Authentication;

public sealed class RefreshTokenServiceTests
{
    [Theory]
    [InlineData("update")]
    [InlineData("concurrency")]
    [InlineData("sqlite")]
    [InlineData("serialization")]
    [InlineData("deadlock")]
    [InlineData("cancellation")]
    public async Task RotateAsync_WhenPersistenceFails_RollsBackAndReturnsInvalidUnlessCancelled(string failure)
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using FailingSaveContext context = new(options);
        await context.Database.EnsureCreatedAsync();
        User user = new()
        {
            Id = Guid.NewGuid(),
            Username = "refresh-failure",
            Email = "refresh-failure@test.com",
            PasswordHash = "test-hash",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        Mock<IAuthenticationService> authentication = new();
        authentication.Setup(service => service.GenerateJwtTokenAsync(It.IsAny<User>())).ReturnsAsync("access-token");
        authentication.Setup(service => service.GetUserWithRolesAndPermissionsAsync(user.Id))
            .ReturnsAsync(new UserDto { Id = user.Id, Username = user.Username, Email = user.Email });
        RefreshTokenService service = new(context, authentication.Object, Mock.Of<IAuthAuditService>(),
            NullLogger<RefreshTokenService>.Instance);
        (string token, _) = await service.CreateAsync(user.Id, "127.0.0.1");
        context.Failure = failure switch
        {
            "update" => new DbUpdateException("Simulated persistence failure"),
            "concurrency" => new DbUpdateConcurrencyException("Simulated concurrency failure"),
            "sqlite" => new SqliteException("Simulated busy database", 5),
            "serialization" => new PostgresException("Simulated serialization failure", "ERROR", "ERROR", "40001"),
            "deadlock" => new PostgresException("Simulated deadlock", "ERROR", "ERROR", "40P01"),
            _ => new OperationCanceledException()
        };

        if (failure == "cancellation")
        {
            Func<Task> rotate = async () => await service.RotateAsync(token, "127.0.0.1");
            await rotate.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            AuthenticationResult result = await service.RotateAsync(token, "127.0.0.1");
            result.Success.Should().BeFalse();
            result.Error.Should().Be("Invalid refresh token.");
            context.ChangeTracker.Entries().Should().BeEmpty();
        }

        context.Failure = null;
        context.ChangeTracker.Clear();
        RefreshToken persisted = await context.RefreshTokens.SingleAsync();
        persisted.IsRevoked.Should().BeFalse();
        persisted.ReplacedByToken.Should().BeNull();
        (await context.RevokedTokens.CountAsync()).Should().Be(0);
    }

    private sealed class FailingSaveContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        public Exception? Failure { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Failure is null ? base.SaveChangesAsync(cancellationToken) : Task.FromException<int>(Failure);
    }
}
