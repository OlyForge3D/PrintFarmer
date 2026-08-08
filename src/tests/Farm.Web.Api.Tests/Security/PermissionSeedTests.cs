using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Web.Api.Services.Startup;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Security;

public sealed class PermissionSeedTests
{
    [Fact]
    public async Task SeedAllAsync_SeedsCalibrationPermissionsIdempotently()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using AppDbContext context = new(options);
        _ = await context.Database.EnsureCreatedAsync();
        var dataSeedService = new Mock<IDataSeedService>(MockBehavior.Loose);
        DatabaseInitializer initializer = new(
            context,
            NullLogger<DatabaseInitializer>.Instance,
            dataSeedService.Object);

        await initializer.SeedAllAsync();
        await initializer.SeedAllAsync();

        string[] expectedActions =
        [
            "acknowledge-bed-clear",
            "cancel",
            "create",
            "delete",
            "generate",
            "manage",
            "promote",
            "publish",
            "read",
            "read-artifact",
            "reconcile",
            "start",
            "submit",
            "update",
            "write",
        ];
        string[] expectedResources =
        [
            PrintFarmerPermissions.Split(PrintFarmerPermissions.Calibration.Create).Resource,
            PrintFarmerPermissions.Split(PrintFarmerPermissions.DispatchSettings.Manage).Resource,
            PrintFarmerPermissions.Split(PrintFarmerPermissions.Queue.Read).Resource,
            PrintFarmerPermissions.Split(PrintFarmerPermissions.Slicing.Submit).Resource,
            PrintFarmerPermissions.Split(PrintFarmerPermissions.Integrations.ManageObico).Resource,
        ];

        foreach (string action in expectedActions)
        {
            _ = (await context.UserActions.CountAsync(candidate => candidate.Name == action))
                .Should().Be(1);
        }

        foreach (string resource in expectedResources)
        {
            _ = (await context.Resources.CountAsync(candidate => candidate.Name == resource))
                .Should().Be(1);
        }

        Guid farmUserRoleId = await context.Roles
            .Where(role => role.Name == "farm_user")
            .Select(role => role.Id)
            .SingleAsync();
        string[] foundationResources = ["calibration", "queue", "slicing", "dispatch-settings"];
        bool farmUserHasImplicitFoundationGrant = await context.RolePermissions
            .AnyAsync(permission =>
                permission.RoleId == farmUserRoleId
                && permission.Granted
                && foundationResources.Contains(permission.Resource.Name));
        _ = farmUserHasImplicitFoundationGrant.Should().BeFalse();
    }

    [Fact]
    public void MatchesUniqueConstraintViolation_UsesProviderErrorCodes()
    {
        DatabaseInitializer.MatchesUniqueConstraintViolation(null, null, null, null, 2067).Should().BeTrue();
        DatabaseInitializer.MatchesUniqueConstraintViolation(null, null, null, null, 1555).Should().BeTrue();
        DatabaseInitializer.MatchesUniqueConstraintViolation("23505", null, null, null, null).Should().BeTrue();
        DatabaseInitializer.MatchesUniqueConstraintViolation(null, 2601, null, null, null).Should().BeTrue();
        DatabaseInitializer.MatchesUniqueConstraintViolation(null, 2627, null, null, null).Should().BeTrue();
        DatabaseInitializer.MatchesUniqueConstraintViolation(null, null, 1062, null, null).Should().BeTrue();

        DatabaseInitializer.MatchesUniqueConstraintViolation("23503", 547, null, null, null).Should().BeFalse();
    }

    [Fact]
    public async Task SeedAllAsync_WhenRunBySeparateContexts_RemainsIdempotent()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (AppDbContext schemaContext = new(options))
        {
            _ = await schemaContext.Database.EnsureCreatedAsync();
        }

        var dataSeedService = new Mock<IDataSeedService>(MockBehavior.Loose);
        await using (AppDbContext firstContext = new(options))
        {
            DatabaseInitializer firstInitializer = new(
                firstContext,
                NullLogger<DatabaseInitializer>.Instance,
                dataSeedService.Object);
            await firstInitializer.SeedAllAsync();
        }

        await using (AppDbContext secondContext = new(options))
        {
            DatabaseInitializer secondInitializer = new(
                secondContext,
                NullLogger<DatabaseInitializer>.Instance,
                dataSeedService.Object);
            await secondInitializer.SeedAllAsync();

            (await secondContext.Resources.CountAsync()).Should().Be(15);
            (await secondContext.UserActions.CountAsync()).Should().Be(17);
            (await secondContext.Roles.CountAsync()).Should().Be(2);
        }
    }

    [Fact]
    public async Task SeedAllAsync_WhenStartedConcurrently_DoesNotDuplicateResources()
    {
        string connectionString = $"Data Source=file:seed_race_{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using SqliteConnection keeper = new(connectionString);
        await keeper.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (AppDbContext schemaContext = new(options))
        {
            _ = await schemaContext.Database.EnsureCreatedAsync();
        }

        var dataSeedService = new Mock<IDataSeedService>(MockBehavior.Loose);
        await using AppDbContext firstContext = new(options);
        await using AppDbContext secondContext = new(options);
        DatabaseInitializer firstInitializer = new(
            firstContext,
            NullLogger<DatabaseInitializer>.Instance,
            dataSeedService.Object);
        DatabaseInitializer secondInitializer = new(
            secondContext,
            NullLogger<DatabaseInitializer>.Instance,
            dataSeedService.Object);

        await Task.WhenAll(firstInitializer.SeedAllAsync(), secondInitializer.SeedAllAsync());

        await using AppDbContext verificationContext = new(options);
        (await verificationContext.Resources.CountAsync()).Should().Be(15);
        (await verificationContext.Resources.Select(resource => resource.Name).Distinct().CountAsync()).Should().Be(15);
    }

    [Fact]
    public async Task SeedAllAsync_WhenResourceInsertWinsRace_ReloadsAndCompletes()
    {
        string connectionString = $"Data Source=file:seed_interceptor_{Guid.NewGuid():N}?mode=memory&cache=shared";
        await using SqliteConnection keeper = new(connectionString);
        await keeper.OpenAsync();
        DbContextOptions<AppDbContext> contenderOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (AppDbContext schemaContext = new(contenderOptions))
        {
            _ = await schemaContext.Database.EnsureCreatedAsync();
        }

        DbContextOptions<AppDbContext> initializerOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new ResourceInsertInterceptor(contenderOptions))
            .Options;
        var dataSeedService = new Mock<IDataSeedService>(MockBehavior.Loose);
        await using AppDbContext context = new(initializerOptions);
        DatabaseInitializer initializer = new(
            context,
            NullLogger<DatabaseInitializer>.Instance,
            dataSeedService.Object);

        await initializer.SeedAllAsync();

        (await context.Resources.CountAsync()).Should().Be(15);
    }

    private sealed class ResourceInsertInterceptor(DbContextOptions<AppDbContext> contenderOptions) : SaveChangesInterceptor
    {
        private int _hasInserted;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _hasInserted, 1) == 0)
            {
                await using AppDbContext contender = new(contenderOptions);
                _ = contender.Resources.Add(new Resource
                {
                    Id = Guid.NewGuid(),
                    Name = "printers",
                    DisplayName = "Printers",
                    Description = "3D printer management",
                    ResourceType = "printer",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
                _ = await contender.SaveChangesAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
