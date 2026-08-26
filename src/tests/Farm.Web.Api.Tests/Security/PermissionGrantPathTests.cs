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
/// was permissions enforceable in code with zero role able to ever hold them — reachable only
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
/// role's ordinary — not bypass — grants.
///
/// Every enforced permission — including allowlisted ones — must still name a real seeded
/// <see cref="Resource"/>, so a typo'd resource name (which would silently fail to receive even
/// the farm_admin blanket grant) is always caught, regardless of whether the permission is
/// otherwise expected to be admin-only.
///
/// This also verifies joint satisfiability: ASP.NET Core combines multiple
/// <c>[RequirePermission]</c> attributes declared on the same class/method with AND semantics, so
/// a member gated by two permissions is only actually usable if a single role can hold both
/// simultaneously — reachability of each permission individually is not sufficient.
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

        [PrintFarmerPermissions.Queue.Reconcile] =
            "Triggers a farm-wide orphaned-job sync (JobQueueController.SyncOrphanedJobsAsync) " +
            "with no per-printer/per-group authorization scoping — reviewed and deliberately " +
            "kept farm_admin-only during issue #1453's grant-path reconciliation.",

        [PrintFarmerPermissions.DispatchSettings.Manage] =
            "Gates the singleton, system-wide auto-dispatch configuration " +
            "(DispatchSettingsController) with no per-printer/per-group scoping — reviewed and " +
            "deliberately kept farm_admin-only during issue #1453's grant-path reconciliation.",
    };

    private static readonly Assembly[] ScannedAssemblies =
    [
        typeof(Farm.Web.Api.Controllers.PrintersController).Assembly,
        typeof(Farm.Infrastructure.Services.SignalR.HarvestHub).Assembly,
        typeof(Farm.Slicer.Module.Api.Controllers.WorkersController).Assembly,
        // Issue #2040: Farm.Modules.PrintQueue controllers (JobQueueController,
        // SlicePrintBridgeController, PrintApprovalsController, DispatchSettingsController, etc.)
        // moved out of Farm.Web.Api and must be scanned here explicitly, or their
        // [RequirePermission] sites — including the AdminOnlyAllowlist entries for
        // Queue.Reconcile and DispatchSettings.Manage above — silently drop out of this guard.
        typeof(Farm.Web.Api.Controllers.JobQueueController).Assembly,
        typeof(Farm.Web.Api.Controllers.CalibrationProjectsController).Assembly,
    ];

    [Fact]
    public async Task EveryEnforcedPermission_HasAGrantPathOrDocumentedAllowlistEntry()
    {
        List<PermissionSite> sites = DiscoverEnforcedPermissionSites();
        sites.Should().NotBeEmpty("the scanned assemblies are expected to contain live [RequirePermission] attributes");

        HashSet<(string Resource, string Action)> enforced = new(
            sites.Select(site => (site.Resource, site.Action)));

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

        // Every (resource, action) granted, per non-admin role. Grouped by role so joint
        // satisfiability (below) can ask "can any *single* role hold this whole set?" rather than
        // "is each permission reachable by *some* role, possibly a different one for each".
        List<(Guid RoleId, string Resource, string Action)> nonAdminRoleGrantRows =
            (await context.RolePermissions
                .Where(rp => rp.RoleId != adminRoleId && rp.Granted)
                .Select(rp => new { rp.RoleId, rp.Resource.Name, ActionName = rp.Action.Name })
                .ToListAsync())
                .Select(x => (x.RoleId, x.Name, x.ActionName))
                .ToList();

        HashSet<(string Resource, string Action)> nonAdminRoleGrants = new(
            nonAdminRoleGrantRows.Select(row => (row.Resource, row.Action)));

        List<string> missingGrantPath = [];
        List<string> unknownResource = [];
        List<string> unsatisfiableCombinations = [];

        foreach ((string resource, string action) in enforced)
        {
            string permission = $"{resource}:{action}";

            if (!seededResourceNames.Contains(resource))
            {
                unknownResource.Add(permission);
            }

            if (string.Equals(action, PrintFarmerPermissions.AdminAction, StringComparison.Ordinal))
            {
                // farm_admin's blanket per-resource "admin" grant (seeded for every resource)
                // makes this trivially reachable, so no further check is needed once the
                // resource itself is confirmed real (checked above for every permission).
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

        // Joint satisfiability: for every member (class or method) gated by more than one
        // [RequirePermission], confirm a single non-admin role can hold every permission in the
        // group at once, or that every permission in the group is individually admin-action or
        // allowlisted admin-only (so the whole member is, correctly, farm_admin-only). A mixed
        // group — some permissions reachable by farm_user, one that is admin-action or
        // allowlisted admin-only — makes the member unreachable by any non-admin role even
        // though each permission looks fine in isolation, so it must NOT be treated as
        // satisfied: the per-permission check below deliberately does not special-case
        // admin-action or the allowlist, only real non-admin RolePermission rows count.
        foreach (IGrouping<string, PermissionSite> group in sites.GroupBy(site => site.Member, StringComparer.Ordinal))
        {
            (string Resource, string Action)[] permissions = group
                .Select(site => (site.Resource, site.Action))
                .Distinct()
                .ToArray();

            if (permissions.Length < 2)
            {
                continue;
            }

            bool allAdminOnlyByAllowlist = permissions.All(p =>
                string.Equals(p.Action, PrintFarmerPermissions.AdminAction, StringComparison.Ordinal)
                || AdminOnlyAllowlist.ContainsKey($"{p.Resource}:{p.Action}"));

            if (allAdminOnlyByAllowlist)
            {
                continue;
            }

            bool satisfiableBySingleRole = nonAdminRoleGrantRows
                .Select(row => row.RoleId)
                .Distinct()
                .Any(roleId => permissions.All(p =>
                    nonAdminRoleGrantRows.Any(row => row.RoleId == roleId
                        && row.Resource == p.Resource
                        && (row.Action == p.Action || row.Action == PrintFarmerPermissions.AdminAction))));

            if (!satisfiableBySingleRole)
            {
                unsatisfiableCombinations.Add(
                    $"{group.Key} requires [{string.Join(", ", permissions.Select(p => $"{p.Resource}:{p.Action}"))}] " +
                    "together, but no single non-admin role can hold every permission in that set");
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

        unsatisfiableCombinations.Should().BeEmpty(
            "ASP.NET Core combines multiple [RequirePermission] attributes on the same member " +
            "with AND semantics, so every permission required by one member must be holdable by " +
            "a single role at once, not merely reachable individually by possibly-different " +
            "roles. Fix: grant the missing permission(s) in the combination to the same role " +
            "(usually farm_user), or allowlist every permission in the group as admin-only if " +
            $"the whole member should be farm_admin-only. Unsatisfiable: {string.Join("; ", unsatisfiableCombinations)}");
    }

    /// <summary>
    /// Answers directly: does <c>farm_admin</c> actually hold the 17 finer-grained permissions
    /// (calibration/queue/slicing/dispatch-settings/obico) that <c>SeedRolePermissionsAsync</c>
    /// never writes a row for?
    ///
    /// This evaluates authority with <see cref="PrintFarmerPermissions.SetGrantsPermission"/> —
    /// the row-only rule, which has NO <see cref="PrintFarmerPermissions.FarmAdminRole"/> bypass —
    /// so the answer rests solely on the seeded rows and the same-resource admin implication.
    ///
    /// That is deliberately stricter than any live path. Every enforcement point short-circuits on
    /// the farm_admin role first: <c>PermissionAuthorizationHandler</c>, <c>HasPermission</c>,
    /// SignalR hubs, the capability services, and — verified during review — both Desktop API-key
    /// paths, where <c>DesktopScopePermissionMap.ResolveEffectiveScopes</c> tests
    /// <c>isOwnerFarmAdmin</c> before <c>SetGrantsPermission</c> and
    /// <c>UserApiKeysController.ValidateOwnerScopeAuthorizationAsync</c> authorizes a farm_admin
    /// owner before it loads denies. So no production path depends on this rule for farm_admin.
    ///
    /// Asserting it anyway is the point: it proves the seeded rows are sufficient on their own
    /// merits, independent of the bypass. The guarantee therefore survives the bypass being
    /// narrowed or removed, and it pins the claim that the 17 missing rows are redundant rather
    /// than merely masked by a role check. Seeding them would be duplication that must be repeated
    /// for every resource added later.
    /// </summary>
    [Fact]
    public async Task SeededFarmAdminRows_SatisfyEveryEnforcedPermission_WithoutTheRoleBypass()
    {
        List<PermissionSite> sites = DiscoverEnforcedPermissionSites();
        HashSet<(string Resource, string Action)> enforced = new(
            sites.Select(site => (site.Resource, site.Action)));

        // Guard against a vacuous pass: this test exists for the finer-grained actions that have
        // no farm_admin row at all. If those ever stop being discovered, the assertion below would
        // trivially hold over nothing but ':admin' permissions and prove nothing.
        enforced.Where(e => !string.Equals(e.Action, PrintFarmerPermissions.AdminAction, StringComparison.Ordinal))
            .Should().NotBeEmpty("the finer-grained actions are the entire point of this test");
        enforced.Should().Contain(("queue", "read"));
        enforced.Should().Contain(("calibration", "create"));

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

        Guid adminRoleId = await context.Roles
            .Where(role => role.Name == PrintFarmerPermissions.FarmAdminRole)
            .Select(role => role.Id)
            .SingleAsync();

        List<(string Resource, string Action, bool Granted)> adminRows =
            (await context.RolePermissions
                .Where(rp => rp.RoleId == adminRoleId)
                .Select(rp => new { rp.Resource.Name, ActionName = rp.Action.Name, rp.Granted })
                .ToListAsync())
                .Select(x => (x.Name, x.ActionName, x.Granted))
                .ToList();

        HashSet<string> granted = new(
            adminRows.Where(r => r.Granted).Select(r => $"{r.Resource}:{r.Action}"),
            StringComparer.Ordinal);
        HashSet<string> denied = new(
            adminRows.Where(r => !r.Granted).Select(r => $"{r.Resource}:{r.Action}"),
            StringComparer.Ordinal);

        List<string> notSatisfied = enforced
            .Select(e => $"{e.Resource}:{e.Action}")
            .Where(permission => !PrintFarmerPermissions.SetGrantsPermission(granted, denied, permission))
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToList();

        notSatisfied.Should().BeEmpty(
            "farm_admin's seeded '{resource}:admin' rows must satisfy every enforced permission " +
            "through the same-resource admin implication, on the strictest row-only path that " +
            "ignores the farm_admin role bypass. A permission listed here is one an administrator " +
            "genuinely cannot exercise via a Desktop API key, and would need either a real seeded " +
            $"row or a documented reason. Not satisfied: {string.Join(", ", notSatisfied)}");
    }

    private static List<PermissionSite> DiscoverEnforcedPermissionSites()
    {
        List<PermissionSite> sites = [];

        foreach (Assembly assembly in ScannedAssemblies)
        {
            foreach (Type type in assembly.GetTypes())
            {
                string typeDisplayName = type.FullName ?? type.Name;
                CollectPermissions(type, typeDisplayName, sites);

                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.DeclaredOnly))
                {
                    // Class-level and method-level [RequirePermission] attributes both gate the
                    // same routed method (ASP.NET Core AND-combines requirements across the
                    // whole authorization pipeline for one action), so a method's group must
                    // include any permission declared on its declaring type as well.
                    string memberDisplayName = $"{typeDisplayName}.{method.Name}";
                    CollectPermissions(type, memberDisplayName, sites);
                    CollectPermissions(method, memberDisplayName, sites);
                }
            }
        }

        return sites;
    }

    private static void CollectPermissions(MemberInfo member, string memberDisplayName, List<PermissionSite> sites)
    {
        foreach (RequirePermissionAttribute attribute in member.GetCustomAttributes<RequirePermissionAttribute>(inherit: false))
        {
            sites.Add(new PermissionSite(memberDisplayName, attribute.Resource, attribute.Action));
        }
    }

    private sealed record PermissionSite(string Member, string Resource, string Action);
}
