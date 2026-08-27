using System.Reflection;
using Farm.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Regression coverage for issue #1451: proves the mechanical migration from
/// <c>[Authorize(Roles = "farm_admin")]</c> / <c>[Authorize(Roles = PrintFarmerPermissions.FarmAdminRole)]</c>
/// to <c>[RequirePermission]</c> is complete. Scans every type and method in the API, shared
/// infrastructure, and slicer-module assemblies for a real, live <see cref="AuthorizeAttribute"/>
/// whose <see cref="AuthorizeAttribute.Roles"/> still names <c>farm_admin</c>. If this test ever
/// fails, someone reintroduced a role-name authorization gate that a custom role can never
/// satisfy, no matter which permissions it holds.
/// </summary>
public sealed class RoleToPermissionMigrationCompletenessTests
{
    private static readonly Assembly[] ScannedAssemblies =
    [
        typeof(Farm.Web.Api.Controllers.PrintersController).Assembly,
        typeof(Farm.Infrastructure.Services.SignalR.HarvestHub).Assembly,
        typeof(Farm.Slicer.Module.Api.Controllers.WorkersController).Assembly,
        // Issue #2040: Farm.Modules.PrintQueue controllers moved out of Farm.Web.Api and must be
        // scanned here explicitly, or a reintroduced farm_admin role-name gate on one of them
        // would silently escape this guard.
        typeof(Farm.Web.Api.Controllers.JobQueueController).Assembly,
        typeof(Farm.Web.Api.Controllers.CalibrationProjectsController).Assembly,
        typeof(Farm.Web.Api.Controllers.Admin.RolesController).Assembly, // Farm.Modules.Identity
        // Issue #2043: Farm.Modules.Devices controllers moved out of Farm.Web.Api and must be
        // scanned here explicitly, or a reintroduced farm_admin role-name gate on one of them
        // would silently escape this guard. AdminHomeAssistantController (also named in the
        // issue) ended up owned by Farm.Modules.Administration instead (Phase 14, #2042, landed
        // first), so NfcController anchors the Devices assembly here instead.
        typeof(Farm.Web.Api.Controllers.NfcController).Assembly,
        // Phase 14 (#2042) never added its own anchor to this guard; the gap was masked
        // pre-merge because AdminHomeAssistantController.Assembly happened to resolve to
        // Farm.Web.Api like every other anchor here. Now that it resolves to
        // Farm.Modules.Administration, added explicitly so that assembly stays scanned.
        typeof(Farm.Web.Api.Controllers.Admin.AdminHomeAssistantController).Assembly,
        // Issue #2088: Farm.Modules.Gcode and Farm.Modules.Inventory were extracted by the
        // module-decomposition epic (#2019, Phases 11/16) but never got an anchor here, so a
        // reintroduced farm_admin role-name gate on either module's controllers would silently
        // escape this guard.
        typeof(Farm.Web.Api.Controllers.GcodeFilesController).Assembly, // Farm.Modules.Gcode
        typeof(Farm.Web.Api.Controllers.PartsInventoryController).Assembly, // Farm.Modules.Inventory
    ];

    [Fact]
    public void NoType_HasFarmAdminRoleNameAuthorization()
    {
        List<string> offenders = [];

        foreach (Assembly assembly in ScannedAssemblies)
        {
            foreach (Type type in assembly.GetTypes())
            {
                CollectFarmAdminRoleOffenses(type, type.FullName ?? type.Name, offenders);

                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.DeclaredOnly))
                {
                    CollectFarmAdminRoleOffenses(
                        method,
                        $"{type.FullName ?? type.Name}.{method.Name}",
                        offenders);
                }
            }
        }

        offenders.Should().BeEmpty(
            "issue #1451 requires every farm_admin role-name gate to be a [RequirePermission] " +
            $"grant instead, but found: {string.Join(", ", offenders)}");
    }

    private static void CollectFarmAdminRoleOffenses(
        MemberInfo member,
        string memberDisplayName,
        List<string> offenders)
    {
        foreach (AuthorizeAttribute attribute in member.GetCustomAttributes<AuthorizeAttribute>(inherit: false))
        {
            if (string.IsNullOrWhiteSpace(attribute.Roles))
            {
                continue;
            }

            string[] roles = attribute.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (roles.Contains(PrintFarmerPermissions.FarmAdminRole, StringComparer.Ordinal))
            {
                offenders.Add(memberDisplayName);
            }
        }
    }
}
