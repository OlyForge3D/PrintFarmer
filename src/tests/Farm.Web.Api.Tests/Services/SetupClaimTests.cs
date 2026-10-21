using Farm.Infrastructure.Contracts.Setup;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Users;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Services;

public sealed class SetupClaimTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.GetFullPath($"bootstrap-claim-{Guid.NewGuid():N}.db");
    private DbContextOptions<AppDbContext> Options => new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={_databasePath};Pooling=False;Default Timeout=30").Options;
    private Guid _roleId;

    public async Task InitializeAsync()
    {
        await using var db = new AppDbContext(Options);
        await db.Database.EnsureCreatedAsync();
        var role = new Role { Id = Guid.NewGuid(), Name = "farm_admin", DisplayName = "Admin", IsActive = true };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        _roleId = role.Id;
    }

    public Task DisposeAsync()
    {
        File.Delete(_databasePath);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TryCreateInitialAdminAsync_ConcurrentContexts_OnlyOneClaimCommits()
    {
        using var barrier = new Barrier(2);
        async Task<bool> ClaimAsync(string name)
        {
            await using var db = new AppDbContext(Options);
            barrier.SignalAndWait();
            return await new EfUsersRepository(db).TryCreateInitialAdminAsync(NewUser(name), _roleId);
        }

        bool[] results = await Task.WhenAll(
            Task.Run(() => ClaimAsync("first")),
            Task.Run(() => ClaimAsync("second")));

        Assert.Single(results, result => result);
        await using var verify = new AppDbContext(Options);
        Assert.Equal(1, await verify.Users.CountAsync());
        Assert.Equal(1, await verify.UserRoles.CountAsync());
        Assert.Equal(1, await verify.AppSettingsEntities.CountAsync(s => s.Key == "Bootstrap.InitialAdmin.Consumed"));
    }

    [Fact]
    public async Task TryCreateInitialAdminAsync_ReplayAfterAdminRemoval_ClaimRemainsConsumed()
    {
        await using (var db = new AppDbContext(Options))
        {
            Assert.True(await new EfUsersRepository(db).TryCreateInitialAdminAsync(NewUser("first"), _roleId));
            await db.UserRoles.ExecuteDeleteAsync();
            await db.Users.ExecuteDeleteAsync();
        }

        await using var replay = new AppDbContext(Options);
        Assert.False(await new EfUsersRepository(replay).TryCreateInitialAdminAsync(NewUser("second"), _roleId));
        Assert.False(await replay.Users.AnyAsync());
    }

    [Fact]
    public async Task TryCreateInitialAdminAsync_ExistingAdminWithoutClaim_DoesNotCreateAnother()
    {
        await using var db = new AppDbContext(Options);
        var repository = new EfUsersRepository(db);
        await repository.AddUserWithRoleAsync(NewUser("existing"), _roleId);

        Assert.False(await repository.TryCreateInitialAdminAsync(NewUser("second"), _roleId));
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    public async Task CreateInitialAdminAsync_UnconfiguredProof_FailsClosed(string? configuredProof)
    {
        await using var db = new AppDbContext(Options);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Setup:BootstrapSecret"] = configuredProof }).Build();
        var service = new SetupService(
            new EfUsersRepository(db),
            Mock.Of<IAuthenticationService>(),
            Mock.Of<IPasswordHashingService>(),
            NullLogger<SetupService>.Instance,
            configuration);

        var result = await service.CreateInitialAdminAsync(new CreateInitialAdminRequest
        {
            Username = "untrusted",
            Email = "untrusted@example.test",
            Password = "ValidPassword123!",
            BootstrapSecret = configuredProof
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(await db.Users.AnyAsync());
        Assert.False(await db.UserRoles.AnyAsync());
    }

    [Fact]
    public async Task TryCreateInitialAdminAsync_UserInsertFailure_DoesNotConsumeClaim()
    {
        await using (var db = new AppDbContext(Options))
        {
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                new EfUsersRepository(db).TryCreateInitialAdminAsync(NewUser("failed"), Guid.NewGuid()));
        }

        await using var retry = new AppDbContext(Options);
        Assert.True(await new EfUsersRepository(retry).TryCreateInitialAdminAsync(NewUser("retry"), _roleId));
        Assert.Equal(1, await retry.Users.CountAsync());
    }

    private static User NewUser(string name) => new()
    {
        Id = Guid.NewGuid(),
        Username = name,
        Email = $"{name}@example.test",
        PasswordHash = "test-hash",
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
}
