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
    /// <summary>
    /// The two CalibrationFoundation permissions deliberately excluded from farm_user's default
    /// grants: both gate unscoped, farm-wide administrative actions (see
    /// PermissionGrantPathTests.AdminOnlyAllowlist for the full written rationale) and remain
    /// farm_admin-only by design.
    /// </summary>
    private static readonly HashSet<string> DeliberatelyAdminOnly = new(StringComparer.Ordinal)
    {
        PrintFarmerPermissions.Queue.Reconcile,
        PrintFarmerPermissions.DispatchSettings.Manage,
    };

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

        // Issue #1453: farm_user's grants must be reconciled against
        // PrintFarmerPermissions.CalibrationFoundation, the single source of truth for the
        // calibration/queue/slicing/dispatch-settings permissions #945 introduced. Previously
        // this assertion required farm_user to have *none* of these grants, which is exactly
        // the gap #1453 closes: these permissions were enforced in code but reachable by no
        // role except the farm_admin bypass. See PermissionGrantPathTests for the general guard
        // that every enforced [RequirePermission] has a real grant path.
        //
        // Two of the sixteen are deliberately excluded from farm_user's grant, per review
        // feedback on #1453: "queue:reconcile" (farm-wide orphaned-job sync,
        // JobQueueController.SyncOrphanedJobsAsync) and "dispatch-settings:manage" (singleton
        // system-wide auto-dispatch config, DispatchSettingsController) are unscoped
        // administrative actions with no per-printer/per-group authorization, so they remain
        // farm_admin-only and are documented in PermissionGrantPathTests.AdminOnlyAllowlist
        // instead of granted here.
        Guid farmUserRoleId = await context.Roles
            .Where(role => role.Name == "farm_user")
            .Select(role => role.Id)
            .SingleAsync();
        var farmUserGrantedPermissions = await context.RolePermissions
            .Where(permission => permission.RoleId == farmUserRoleId && permission.Granted)
            .Select(permission => permission.Resource.Name + ":" + permission.Action.Name)
            .ToListAsync();

        foreach (string permission in PrintFarmerPermissions.CalibrationFoundation)
        {
            if (DeliberatelyAdminOnly.Contains(permission))
            {
                _ = farmUserGrantedPermissions.Should().NotContain(
                    permission,
                    $"{permission} is deliberately farm_admin-only (unscoped, farm-wide " +
                    "administrative action) and must not be granted to farm_user by default");
                continue;
            }

            _ = farmUserGrantedPermissions.Should().Contain(
                permission,
                $"farm_user must hold {permission} — #945 enforced it in code without ever " +
                "granting it to any non-admin role");
        }
    }

    [Fact]
    public async Task SeedAllAsync_WhenReseedingExistingDeployment_IsAdditiveAndPreservesCustomGrants()
    {
        // Issue #1453 upgrade-path guard: simulate a pre-fix deployment — farm_user grants exist
        // but are missing the CalibrationFoundation subset this issue adds, and a custom
        // RolePermission row exists that SeedRolePermissionsAsync's static list knows nothing
        // about. Reseeding must be purely additive: the custom row must survive untouched, and
        // exactly the expected new grants must appear, without disturbing anything else.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (AppDbContext seedContext = new(options))
        {
            _ = await seedContext.Database.EnsureCreatedAsync();
            var seedDataSeedService = new Mock<IDataSeedService>(MockBehavior.Loose);
            DatabaseInitializer seedInitializer = new(
                seedContext,
                NullLogger<DatabaseInitializer>.Instance,
                seedDataSeedService.Object);

            // Seed once to populate resources/actions/roles as they would exist pre-fix, then
            // remove the CalibrationFoundation grants this issue adds to simulate the stale
            // pre-#1453 farm_user permission set.
            await seedInitializer.SeedAllAsync();
        }

        Guid customPermissionId;
        await using (AppDbContext mutateContext = new(options))
        {
            Guid farmUserRoleId = await mutateContext.Roles
                .Where(role => role.Name == "farm_user")
                .Select(role => role.Id)
                .SingleAsync();

            IQueryable<RolePermission> staleGrants = mutateContext.RolePermissions
                .Where(rp => rp.RoleId == farmUserRoleId
                    && PrintFarmerPermissions.CalibrationFoundation.Contains(rp.Resource.Name + ":" + rp.Action.Name));
            mutateContext.RolePermissions.RemoveRange(staleGrants);

            // A synthetic custom grant unrelated to SeedRolePermissionsAsync's static list —
            // represents a role/permission combination a real deployment might have configured
            // by hand (e.g. via the admin grant UI) that reseeding must never touch or remove.
            Resource printersResource = await mutateContext.Resources.SingleAsync(r => r.Name == "printers");
            UserAction deleteAction = await mutateContext.UserActions.SingleAsync(a => a.Name == "delete");
            RolePermission customGrant = new()
            {
                Id = Guid.NewGuid(),
                RoleId = farmUserRoleId,
                ResourceId = printersResource.Id,
                ActionId = deleteAction.Id,
                Granted = true,
                CreatedAt = DateTime.UtcNow,
            };
            _ = mutateContext.RolePermissions.Add(customGrant);
            _ = await mutateContext.SaveChangesAsync();
            customPermissionId = customGrant.Id;
        }

        await using (AppDbContext reseedContext = new(options))
        {
            var reseedDataSeedService = new Mock<IDataSeedService>(MockBehavior.Loose);
            DatabaseInitializer reseedInitializer = new(
                reseedContext,
                NullLogger<DatabaseInitializer>.Instance,
                reseedDataSeedService.Object);

            await reseedInitializer.SeedAllAsync();
        }

        await using AppDbContext verifyContext = new(options);
        Guid verifyFarmUserRoleId = await verifyContext.Roles
            .Where(role => role.Name == "farm_user")
            .Select(role => role.Id)
            .SingleAsync();

        // The synthetic custom grant must be untouched — same row, still granted.
        RolePermission? survivingCustomGrant = await verifyContext.RolePermissions
            .FirstOrDefaultAsync(rp => rp.Id == customPermissionId);
        _ = survivingCustomGrant.Should().NotBeNull(
            "reseeding must be additive and must never remove a pre-existing custom grant");
        _ = survivingCustomGrant!.Granted.Should().BeTrue();

        var farmUserGrantedPermissions = await verifyContext.RolePermissions
            .Where(permission => permission.RoleId == verifyFarmUserRoleId && permission.Granted)
            .Select(permission => permission.Resource.Name + ":" + permission.Action.Name)
            .ToListAsync();

        // The custom grant survives...
        _ = farmUserGrantedPermissions.Should().Contain("printers:delete");

        // ...and every non-admin-only CalibrationFoundation permission was re-added by the
        // additive reseed, proving this isn't merely idempotent-from-empty but genuinely
        // additive against a stale pre-existing deployment.
        foreach (string permission in PrintFarmerPermissions.CalibrationFoundation)
        {
            if (DeliberatelyAdminOnly.Contains(permission))
            {
                continue;
            }

            _ = farmUserGrantedPermissions.Should().Contain(
                permission,
                $"reseeding an existing deployment must add {permission} to farm_user even " +
                "though the deployment predates #1453 and never had it");
        }
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

            (await secondContext.Resources.CountAsync()).Should().Be(37);
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
        (await verificationContext.Resources.CountAsync()).Should().Be(37);
        (await verificationContext.Resources.Select(resource => resource.Name).Distinct().CountAsync()).Should().Be(37);
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

        (await context.Resources.CountAsync()).Should().Be(37);
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
