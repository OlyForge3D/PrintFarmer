using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Infrastructure.Authorization;
using Farm.Web.Api.Services.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Admin;

/// <summary>
/// Unit tests for <see cref="PermissionCatalogService"/>. These verify that the catalog is
/// derived purely from <see cref="EndpointDataSource"/> metadata (not a hardcoded list),
/// that both controller-level and method-level <see cref="RequirePermissionAttribute"/>
/// placements are enumerated, that every enforced permission appears exactly once with its
/// gating routes, and that database catalog rows unmatched by any endpoint are reported as
/// orphaned rather than silently dropped.
/// </summary>
public class PermissionCatalogServiceTests
{
    [Fact]
    public async Task GetCatalogAsync_EnumeratesMethodLevelAttribute_ExactlyOnce()
    {
        // Simulates a controller action carrying its own [RequirePermission], with no
        // class-level attribute present on the endpoint's metadata.
        RouteEndpoint endpoint = BuildEndpoint(
            "api/printers/{id}/execute",
            ["POST"],
            new RequirePermissionAttribute("printers", "execute"));

        await using AppDbContext db = CreateContext();
        await SeedCatalogAsync(db, ("printers", "Printers", "execute", "Execute"));

        PermissionCatalogDto catalog = await CreateService(db, endpoint).GetCatalogAsync();

        PermissionCatalogEntryDto entry = catalog.Resources
            .Should().ContainSingle().Which.Permissions.Should().ContainSingle().Which;
        entry.Permission.Should().Be("printers:execute");
        entry.Routes.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PermissionRouteDto { Method = "POST", Template = "api/printers/{id}/execute" });
    }

    [Fact]
    public async Task GetCatalogAsync_ControllerLevelAndMethodLevelAttributesOnSameEndpoint_DistinctPermissions_BothAppearGatingTheSameRoute()
    {
        // ASP.NET Core MVC merges a class-applied [RequirePermission] and a method-applied
        // [RequirePermission] into a single endpoint's metadata collection (RequirePermissionAttribute
        // is AllowMultiple = true). Simulate that merge directly by adding two distinct attributes
        // to one endpoint's metadata, as MVC would for a controller-level + action-level pairing.
        RouteEndpoint endpoint = BuildEndpoint(
            "api/admin/data",
            ["GET"],
            new RequirePermissionAttribute("admin", "execute"),
            new RequirePermissionAttribute("system_settings", "read"));

        await using AppDbContext db = CreateContext();
        await SeedCatalogAsync(db, ("admin", "Administration", "execute", "Execute"));
        await SeedCatalogAsync(db, ("system_settings", "System Settings", "read", "Read"));

        PermissionCatalogDto catalog = await CreateService(db, endpoint).GetCatalogAsync();

        catalog.Resources.Should().HaveCount(2);
        catalog.Resources.SelectMany(r => r.Permissions).Select(p => p.Permission)
            .Should().BeEquivalentTo("admin:execute", "system_settings:read");
        catalog.Resources.SelectMany(r => r.Permissions).Should().OnlyContain(
            p => p.Routes.Count == 1 && p.Routes[0].Template == "api/admin/data" && p.Routes[0].Method == "GET");
    }

    [Fact]
    public async Task GetCatalogAsync_ControllerLevelAndMethodLevelAttributesOnSameEndpoint_SamePermission_ProducesExactlyOneEntryWithOneRoute()
    {
        // Guards against a duplicate [RequirePermission] applied at both class and method level
        // (accidentally or otherwise) fanning out into duplicate catalog entries or routes.
        RouteEndpoint endpoint = BuildEndpoint(
            "api/admin/data",
            ["GET"],
            new RequirePermissionAttribute("admin", "execute"),
            new RequirePermissionAttribute("admin", "execute"));

        await using AppDbContext db = CreateContext();
        await SeedCatalogAsync(db, ("admin", "Administration", "execute", "Execute"));

        PermissionCatalogDto catalog = await CreateService(db, endpoint).GetCatalogAsync();

        catalog.Resources.Should().ContainSingle().Which.Resource.Should().Be("admin");
        PermissionCatalogEntryDto entry = catalog.Resources[0].Permissions.Should().ContainSingle().Which;
        entry.Permission.Should().Be("admin:execute");
        entry.Routes.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PermissionRouteDto { Method = "GET", Template = "api/admin/data" });
    }

    [Fact]
    public async Task GetCatalogAsync_EndpointWithoutHttpMethodMetadata_FallsBackToAnyMethod()
    {
        RouteEndpointBuilder builder = new(
            requestDelegate: _ => Task.CompletedTask,
            routePattern: RoutePatternFactory.Parse("api/legacy-endpoint"),
            order: 0);
        builder.Metadata.Add(new RequirePermissionAttribute("legacy", "read"));
        RouteEndpoint endpoint = (RouteEndpoint)builder.Build();

        await using AppDbContext db = CreateContext();

        PermissionCatalogDto catalog = await CreateService(db, endpoint).GetCatalogAsync();

        catalog.Resources.Should().ContainSingle().Which.Permissions.Should().ContainSingle()
            .Which.Routes.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PermissionRouteDto { Method = "ANY", Template = "api/legacy-endpoint" });
    }

    [Fact]
    public async Task GetCatalogAsync_SamePermissionAcrossMultipleEndpoints_ListsEveryRouteWithoutDuplicatingPermission()
    {
        RouteEndpoint readOne = BuildEndpoint("api/queue", ["GET"], new RequirePermissionAttribute("queue", "read"));
        RouteEndpoint readTwo = BuildEndpoint("api/queue/{id}", ["GET"], new RequirePermissionAttribute("queue", "read"));

        await using AppDbContext db = CreateContext();
        await SeedCatalogAsync(db, ("queue", "Calibration Queue", "read", "Read"));

        PermissionCatalogDto catalog = await CreateService(db, readOne, readTwo).GetCatalogAsync();

        PermissionResourceGroupDto group = catalog.Resources.Should().ContainSingle().Which;
        PermissionCatalogEntryDto entry = group.Permissions.Should().ContainSingle().Which;
        entry.Routes.Should().HaveCount(2);
        entry.Routes.Select(r => r.Template).Should().BeEquivalentTo("api/queue", "api/queue/{id}");
    }

    [Fact]
    public async Task GetCatalogAsync_EndpointWithoutRequirePermission_IsNotIncluded()
    {
        RouteEndpoint anonymous = BuildEndpoint("api/health", ["GET"]);

        await using AppDbContext db = CreateContext();

        PermissionCatalogDto catalog = await CreateService(db, anonymous).GetCatalogAsync();

        catalog.Resources.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCatalogAsync_JoinsResourceAndActionDisplayMetadataFromDatabase()
    {
        RouteEndpoint endpoint = BuildEndpoint(
            "api/calibration",
            ["GET"],
            new RequirePermissionAttribute("calibration", "read"));

        await using AppDbContext db = CreateContext();
        await SeedCatalogAsync(db, ("calibration", "Printer Calibration", "read", "Read"));

        PermissionCatalogDto catalog = await CreateService(db, endpoint).GetCatalogAsync();

        PermissionResourceGroupDto group = catalog.Resources.Should().ContainSingle().Which;
        group.DisplayName.Should().Be("Printer Calibration");
        group.Permissions[0].ActionDisplayName.Should().Be("Read");
    }

    [Fact]
    public async Task GetCatalogAsync_AdminAction_IsNeverReportedAsImpliedByAdmin()
    {
        RouteEndpoint endpoint = BuildEndpoint(
            "api/admin/whatever",
            ["GET"],
            new RequirePermissionAttribute("widgets", "admin"));

        await using AppDbContext db = CreateContext();

        PermissionCatalogDto catalog = await CreateService(db, endpoint).GetCatalogAsync();

        catalog.Resources.Should().ContainSingle().Which.Permissions
            .Should().ContainSingle().Which.ImpliedByAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task GetCatalogAsync_NonAdminAction_IsReportedAsImpliedByAdmin()
    {
        RouteEndpoint endpoint = BuildEndpoint(
            "api/queue",
            ["GET"],
            new RequirePermissionAttribute("queue", "read"));

        await using AppDbContext db = CreateContext();

        PermissionCatalogDto catalog = await CreateService(db, endpoint).GetCatalogAsync();

        catalog.Resources.Should().ContainSingle().Which.Permissions
            .Should().ContainSingle().Which.ImpliedByAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task GetCatalogAsync_RolePermissionRowWithNoEnforcingEndpoint_IsReportedAsOrphaned()
    {
        RouteEndpoint endpoint = BuildEndpoint(
            "api/queue",
            ["GET"],
            new RequirePermissionAttribute("queue", "read"));

        await using AppDbContext db = CreateContext();
        await SeedCatalogAsync(db, ("queue", "Calibration Queue", "read", "Read"));
        await SeedCatalogAsync(db, ("job_queue", "Print Job Queue", "read", "Read"));
        await GrantRolePermissionAsync(db, "job_queue", "read");
        // The enforced permission's own resource/action combination should never appear
        // in the orphaned list even though it also has a granted RolePermission row.
        await GrantRolePermissionAsync(db, "queue", "read");

        PermissionCatalogDto catalog = await CreateService(db, endpoint).GetCatalogAsync();

        catalog.OrphanedCatalogEntries.Should().ContainSingle()
            .Which.Permission.Should().Be("job_queue:read");
    }

    private static PermissionCatalogService CreateService(AppDbContext db, params RouteEndpoint[] endpoints) =>
        new(new FakeEndpointDataSource(endpoints), db);

    private static RouteEndpoint BuildEndpoint(string template, string[] methods, params RequirePermissionAttribute[] permissions)
    {
        RouteEndpointBuilder builder = new(
            requestDelegate: _ => Task.CompletedTask,
            routePattern: RoutePatternFactory.Parse(template),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(methods));
        foreach (RequirePermissionAttribute permission in permissions)
        {
            builder.Metadata.Add(permission);
        }

        return (RouteEndpoint)builder.Build();
    }

    private static AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"permission-catalog-service-{Guid.NewGuid()}")
            .Options;
        return new(options);
    }

    private static async Task SeedCatalogAsync(
        AppDbContext db,
        (string Name, string DisplayName, string ActionName, string ActionDisplayName) seed)
    {
        if (!await db.Resources.AnyAsync(r => r.Name == seed.Name))
        {
            db.Resources.Add(new Resource
            {
                Id = Guid.NewGuid(),
                Name = seed.Name,
                DisplayName = seed.DisplayName,
                ResourceType = "test",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        if (!await db.UserActions.AnyAsync(a => a.Name == seed.ActionName))
        {
            db.UserActions.Add(new UserAction
            {
                Id = Guid.NewGuid(),
                Name = seed.ActionName,
                DisplayName = seed.ActionDisplayName,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task GrantRolePermissionAsync(AppDbContext db, string resourceName, string actionName)
    {
        Role role = await db.Roles.FirstOrDefaultAsync(r => r.Name == "farm_user")
            ?? await CreateRoleAsync(db);
        Resource resource = await db.Resources.FirstAsync(r => r.Name == resourceName);
        UserAction action = await db.UserActions.FirstAsync(a => a.Name == actionName);

        db.RolePermissions.Add(new RolePermission
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            ResourceId = resource.Id,
            ActionId = action.Id,
            Granted = true,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Role> CreateRoleAsync(AppDbContext db)
    {
        Role role = new()
        {
            Id = Guid.NewGuid(),
            Name = "farm_user",
            DisplayName = "Farm User",
            IsSystemRole = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private sealed class FakeEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;

        public override IChangeToken GetChangeToken() => NullChangeToken.Instance;
    }

    private sealed class NullChangeToken : IChangeToken
    {
        public static readonly NullChangeToken Instance = new();

        public bool HasChanged => false;

        public bool ActiveChangeCallbacks => false;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
