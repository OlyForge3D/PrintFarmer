using Farm.Infrastructure.Data;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Web.Api.Services.Startup;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
}
