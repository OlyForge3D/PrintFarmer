using System.Reflection;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Web.Api.Services.Startup;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Guard for issue #1453 (part of epic #1445, FR-5): the check that would have caught #945.
///
/// #945 added the <c>calibration</c>/<c>queue</c>/<c>slicing</c> resources, their actions, and
/// the corresponding <c>[RequirePermission]</c> attributes, but never extended <c>farm_user</c>'s
/// seeded grants (<see cref="DatabaseInitializer"/>'s <c>SeedRolePermissionsAsync</c>). The result
/// was 15 permissions enforceable in code with zero role able to ever hold them — reachable only
/// through the <c>farm_admin</c> role's blanket bypass (<see cref="PrintFarmerPermissions.IsFarmAdmin"/>),
/// which is not a grant path. No test or startup check noticed.
///
/// This test enumerates every live <see cref="RequirePermissionAttribute"/> across the API and
/// slicer-host assemblies (via reflection, not routing, so it also catches SignalR hubs) and
/// asserts each one has a real grant path:
/// <list type="bullet">
/// <item>a seeded non-admin role (today, only <c>farm_user</c>) holds the exact
/// <c>resource:action</c> permission, or holds <c>resource:admin</c> (which implies every other
/// action on that resource per <see cref="PrintFarmerPermissions.ImpliesViaResourceAdmin"/>); or</item>
/// <item>the permission is on <see cref="AdminOnlyAllowlist"/> with a written reason explaining
/// why it is intentionally farm_admin-only.</item>
/// </list>
///
/// <c>{resource}:admin</c> permissions themselves are deliberately excluded from the "must have a
/// non-admin grant path" requirement: <c>SeedRolePermissionsAsync</c> grants <c>farm_admin</c> the
/// <c>admin</c> action on every seeded resource by design (that is what makes farm_admin an
/// administrator), so every <c>resource:admin</c> permission is trivially reachable through that
/// role's ordinary — not bypass — grants. This test still asserts the resource itself is a known
/// seeded <see cref="Resource"/>, so a typo'd resource name on an admin-only endpoint (which would
/// silently fail to receive the blanket grant) is still caught.
/// </summary>
public sealed class PermissionGrantPathTests
{
    /// <summary>
    /// Permissions that are intentionally reachable only by farm_admin, with a written reason.
    /// Each entry here is a deliberate design decision, not an oversight — add to this list only
    /// with a comment explaining why a custom or farm_user role must never hold the permission.
    /// </summary>
    private static readonly Dictionary<string, string> AdminOnlyAllowlist = new(StringComparer.Ordinal)
    {
        [PrintFarmerPermissions.Integrations.ManageObico] =
            "Managing the Obico ML failure-detection integration (server credentials, " +
            "connectivity probes) is an administrative surface by design; see " +
            "PrintFarmerPermissions.Integrations.ManageObico's own doc comment.",
    };

    private static readonly Assembly[] ScannedAssemblies =
    [
        typeof(Farm.Web.Api.Controllers.PrintersController).Assembly,
        typeof(Farm.Infrastructure.Services.SignalR.HarvestHub).Assembly,
        typeof(Farm.Slicer.Module.Api.Controllers.WorkersController).Assembly,
    ];

    [Fact]
    public async Task EveryEnforcedPermission_HasAGrantPathOrDocumentedAllowlistEntry()
    {
        HashSet<(string Resource, string Action)> enforced = DiscoverEnforcedPermissions();
        enforced.Should().NotBeEmpty("the scanned assemblies are expected to contain live [RequirePermission] attributes");

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

        HashSet<string> seededResourceNames = new(
            await context.Resources.Select(resource => resource.Name).ToListAsync(),
            StringComparer.Ordinal);

        Guid adminRoleId = await context.Roles
            .Where(role => role.Name == PrintFarmerPermissions.FarmAdminRole)
            .Select(role => role.Id)
            .SingleAsync();

        // Every (resource, action) granted, for any *non-admin* role. A row here is a real grant
        // path: some role other than farm_admin's blanket bypass can hold it.
        HashSet<(string Resource, string Action)> nonAdminRoleGrants = new(
            (await context.RolePermissions
                .Where(rp => rp.RoleId != adminRoleId && rp.Granted)
                .Select(rp => new { rp.Resource.Name, ActionName = rp.Action.Name })
                .ToListAsync())
                .Select(x => (x.Name, x.ActionName)));

        List<string> missingGrantPath = [];
        List<string> unknownResource = [];

        foreach ((string resource, string action) in enforced)
        {
            string permission = $"{resource}:{action}";

            if (string.Equals(action, PrintFarmerPermissions.AdminAction, StringComparison.Ordinal))
            {
                // farm_admin's blanket per-resource "admin" grant (seeded for every resource)
                // makes this trivially reachable — but only if the resource itself is real.
                if (!seededResourceNames.Contains(resource))
                {
                    unknownResource.Add(permission);
                }

                continue;
            }

            bool hasNonAdminGrant = nonAdminRoleGrants.Contains((resource, action))
                || nonAdminRoleGrants.Contains((resource, PrintFarmerPermissions.AdminAction));
            bool isDocumentedAdminOnly = AdminOnlyAllowlist.ContainsKey(permission);

            if (!hasNonAdminGrant && !isDocumentedAdminOnly)
            {
                missingGrantPath.Add(permission);
            }
        }

        unknownResource.Should().BeEmpty(
            "every [RequirePermission] resource must exist in the seeded Resource catalog " +
            "(DatabaseInitializer.SeedResourcesAsync), otherwise the resource:admin " +
            "permission cannot receive even the farm_admin blanket grant. " +
            $"Add the missing resource(s): {string.Join(", ", unknownResource)}");

        missingGrantPath.Should().BeEmpty(
            "every [RequirePermission] permission must be reachable by at least one non-" +
            "farm_admin role, or be documented in PermissionGrantPathTests.AdminOnlyAllowlist " +
            "with a written reason. A permission that is enforced in code but reachable only " +
            "via the farm_admin bypass is exactly the #945 gap issue #1453 guards against. " +
            "Fix: add a (resource, action) grant for farm_user in DatabaseInitializer." +
            "SeedRolePermissionsAsync (additive — never remove an existing grant), or add a " +
            $"reasoned AdminOnlyAllowlist entry. Missing grant path: {string.Join(", ", missingGrantPath)}");
    }

    private static HashSet<(string Resource, string Action)> DiscoverEnforcedPermissions()
    {
        HashSet<(string, string)> enforced = new();

        foreach (Assembly assembly in ScannedAssemblies)
        {
            foreach (Type type in assembly.GetTypes())
            {
                CollectPermissions(type, enforced);

                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.DeclaredOnly))
                {
                    CollectPermissions(method, enforced);
                }
            }
        }

        return enforced;
    }

    private static void CollectPermissions(MemberInfo member, HashSet<(string, string)> enforced)
    {
        foreach (RequirePermissionAttribute attribute in member.GetCustomAttributes<RequirePermissionAttribute>(inherit: false))
        {
            _ = enforced.Add((attribute.Resource, attribute.Action));
        }
    }
}
