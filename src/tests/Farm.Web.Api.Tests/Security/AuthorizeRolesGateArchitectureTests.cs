using System.Reflection;
using Farm.Slicer.Module.Api;
using Farm.Web.Api.Startup;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Architecture test for issue #1452 (part of epic #1445, FR-4): guards against reintroducing
/// role-name authorization gates now that issue #1451 migrated all 167 pre-existing
/// <c>[Authorize(Roles = "farm_admin")]</c> sites to <c>[RequirePermission(resource, action)]</c>.
///
/// Unlike <see cref="RoleToPermissionMigrationCompletenessTests"/> — which only checks for the
/// specific <c>farm_admin</c> role name left over from the migration — this test fails on
/// <em>any</em> <see cref="AuthorizeAttribute.Roles"/> value set on a <see cref="ControllerBase"/>
/// -derived type (concrete or abstract base) or method, in the main API or the slicer host. A
/// role-name gate is a surface a custom role can never reach, no matter which permissions it
/// holds, so new ones must not accumulate.
///
/// Issue #1467 extended this file with a second guard,
/// <see cref="NoApiController_HasPolicyBasedRoleGate"/>, for the functionally-equivalent bypass
/// Vasquez found during #1452's review: a policy alias such as
/// <c>[Authorize(Policy = "farm_admin")]</c> or <c>[Authorize(Policy = "RequireAdmin")]</c> is
/// just as unreachable by a custom role as a literal <c>Roles = "farm_admin"</c> gate once the
/// named policy resolves (via <c>AuthorizationOptions.GetPolicy</c>) to a requirement set
/// containing a <see cref="RolesAuthorizationRequirement"/> — regardless of what the policy is
/// named. That guard builds the real <see cref="AuthorizationOptions"/> for both the main API
/// (<see cref="AuthenticationStartup.AddPrintFarmerAuthentication"/>) and the slicer host
/// (<see cref="SlicerHostAuthorizationExtensions.AddSlicerHostAuthorization"/>) so it evaluates
/// exactly what each running host would.
///
/// <b>Scope, deliberately matching issue #1452's ask:</b> both guards check REST controllers in
/// the main API (<c>Farm.Web.Api</c>), slicer host (<c>Farm.Slicer.Module.Api</c>), and every
/// <c>Farm.Modules.*</c> module assembly that hosts controllers moved out of the main API by the
/// module-decomposition epic (#2019) — a controller does not stop being an "API controller" for
/// this guard's purposes just because it now lives in its own assembly; each module phase MUST
/// add its controller assembly here (see #2037 review of the Maintenance move, which found the
/// SmartPlug module's <see cref="Farm.Web.Api.Controllers.Admin.AdminPowerMonitorsController"/>
/// had been missed by phase 8). SignalR Hub methods (e.g. <c>HarvestHub</c>) are not
/// <see cref="ControllerBase"/>-derived and are out of scope for "API controllers" as the issue
/// describes them.
/// </summary>
public sealed class AuthorizeRolesGateArchitectureTests
{
    /// <summary>
    /// Explicit, minimal allowlist for genuine policy-alias exceptions — analogous to
    /// <see cref="AllowedRoleGates"/> but for <see cref="NoApiController_HasPolicyBasedRoleGate"/>.
    /// Each entry MUST carry a written reason as an inline comment.
    /// </summary>
    private static readonly HashSet<string> AllowedPolicyRoleGates = new(StringComparer.Ordinal)
    {
        // Intentionally empty: issue #1467 removed every role-backed policy alias
        // (RequireAdmin/farm_admin/CanViewSliceQueue) from the API and slicer host controllers,
        // and deleted the alias registrations themselves. Add an entry here only with a written
        // reason if a genuine exception is ever needed, and keep the list as small as possible.
    };

    private static readonly Assembly[] ScannedAssemblies =
    [
        typeof(Farm.Web.Api.Controllers.PrintersController).Assembly,
        typeof(Farm.Slicer.Module.Api.Controllers.WorkersController).Assembly,
        // Issue #2040: Farm.Modules.PrintQueue is hosted by the main API (Farm.Web.Api), not the
        // slicer host, and must be scanned here for the same reason it was added to
        // QueueEnqueuePermissionArchitectureTests.WalkableAssemblies — controllers that moved out
        // of Farm.Web.Api into a module assembly silently drop out of an assembly-array-based
        // scan unless the new assembly is added explicitly.
        typeof(Farm.Web.Api.Controllers.JobQueueController).Assembly,
        // Module-decomposition epic (#2019): controllers that moved out of Farm.Web.Api into
        // their own assembly are still "API controllers" for this guard's purposes.
        typeof(Farm.Web.Api.Controllers.Admin.AdminPowerMonitorsController).Assembly,
        typeof(Farm.Web.Api.Controllers.MaintenanceController).Assembly,
        typeof(Farm.Web.Api.Controllers.CalibrationProjectsController).Assembly,
        typeof(Farm.Web.Api.Controllers.Admin.RolesController).Assembly,
        // Issue #2043: Farm.Modules.Devices hosts the OctoPrint-compat auth surface plus the
        // NFC/camera/Home-Assistant device controllers (namespaces unchanged, so
        // Farm.Web.Api.Controllers.Admin.AdminHomeAssistantController now resolves from this
        // module assembly). Must be scanned for the same reason PrintQueue/Identity are above.
        typeof(Farm.Web.Api.Controllers.Admin.AdminHomeAssistantController).Assembly,
    ];

    /// <summary>
    /// Assemblies whose controllers are hosted by the MAIN API process (i.e. evaluated against
    /// <see cref="BuildMainApiAuthorizationOptions"/> rather than
    /// <see cref="BuildSlicerHostAuthorizationOptions"/> in
    /// <see cref="NoApiController_HasPolicyBasedRoleGate"/>). This is deliberately a separate,
    /// explicit set rather than "every assembly in <see cref="ScannedAssemblies"/> except the
    /// slicer host one": a module assembly (<c>Farm.Modules.*</c>) is unambiguously main-API-
    /// hosted regardless of how many other non-slicer-host assemblies get added here in future
    /// module phases, so this must never be inferred by elimination against a two-entry list.
    /// Every future <c>Farm.Modules.*</c> entry added to <see cref="ScannedAssemblies"/> MUST
    /// also be added here, or its policy-alias check silently runs against the wrong host's
    /// policy registry and can produce false negatives.
    /// </summary>
    private static readonly HashSet<Assembly> MainApiHostedAssemblies =
    [
        typeof(Farm.Web.Api.Controllers.PrintersController).Assembly,
        typeof(Farm.Web.Api.Controllers.JobQueueController).Assembly,
        typeof(Farm.Web.Api.Controllers.Admin.AdminPowerMonitorsController).Assembly,
        typeof(Farm.Web.Api.Controllers.MaintenanceController).Assembly,
        typeof(Farm.Web.Api.Controllers.CalibrationProjectsController).Assembly,
        typeof(Farm.Web.Api.Controllers.Admin.RolesController).Assembly,
        typeof(Farm.Web.Api.Controllers.Admin.AdminHomeAssistantController).Assembly,
    ];

    [Fact]
    public void NoApiController_HasRoleNameAuthorizationGate()
    {
        List<string> offenders = [];

        foreach (Assembly assembly in ScannedAssemblies)
        {
            // Scan every controller-related type — including abstract base controllers and
            // non-public controllers — not just concrete public leaf controllers. A role-name
            // gate declared on a shared abstract base (e.g. CalibrationControllerBase) applies
            // to every derived concrete controller just as much as one declared directly, and
            // must be caught here too.
            foreach (Type type in assembly.GetTypes())
            {
                if (!typeof(ControllerBase).IsAssignableFrom(type))
                {
                    continue;
                }

                string typeDisplayName = type.FullName ?? type.Name;

                // inherit: false is intentional and safe here: because every ControllerBase-
                // derived type (abstract or concrete) is scanned individually, an attribute
                // declared on a base type is discovered when that base type itself is visited,
                // not via inheritance on a derived type — avoiding duplicate offender reports
                // for the same declared attribute.
                CollectRoleGateOffenses(type, typeDisplayName, offenders);

                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.DeclaredOnly))
                {
                    string parameterSignature = string.Join(
                        ',',
                        method.GetParameters().Select(p => p.ParameterType.Name));
                    CollectRoleGateOffenses(
                        method,
                        $"{typeDisplayName}.{method.Name}({parameterSignature})",
                        offenders);
                }
            }
        }

        offenders.Should().BeEmpty(
            "issue #1452 requires every API controller to gate access with " +
            "[RequirePermission(resource, action)] rather than a role-name " +
            "[Authorize(Roles = ...)] gate — a custom role can never satisfy a role-name gate " +
            "no matter which permissions it holds. Replace [Authorize(Roles = ...)] with " +
            "[RequirePermission(resource, action)] on the offending member(s), or add a " +
            "documented, reasoned entry to AllowedRoleGates if a genuine exception applies. " +
            $"Offenders: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// Issue #1467: guards against <em>policy-based</em> role aliases such as
    /// <c>[Authorize(Policy = "farm_admin")]</c> — functionally identical to a literal
    /// <c>Roles = "farm_admin"</c> gate, just spelled differently. Builds the real
    /// <see cref="AuthorizationOptions"/> for each scanned assembly's host — main API (including
    /// every <c>Farm.Modules.*</c> assembly per <see cref="MainApiHostedAssemblies"/>, since those
    /// controllers execute inside the main API process regardless of which assembly they were
    /// compiled into) or slicer host — and flags any <c>[Authorize(Policy = X)]</c> whose named
    /// policy <c>X</c> resolves to a requirement set containing a
    /// <see cref="RolesAuthorizationRequirement"/>. An unrecognized policy name (one that does
    /// not resolve via <see cref="AuthorizationOptions.GetPolicy(string)"/>) is not flagged here
    /// — that is a wiring bug the app would surface at startup/runtime, not a role-name-gate
    /// bypass.
    /// </summary>
    [Fact]
    public void NoApiController_HasPolicyBasedRoleGate()
    {
        AuthorizationOptions mainApiOptions = BuildMainApiAuthorizationOptions();
        AuthorizationOptions slicerHostOptions = BuildSlicerHostAuthorizationOptions();

        List<string> offenders = [];

        foreach (Assembly assembly in ScannedAssemblies)
        {
            AuthorizationOptions options = MainApiHostedAssemblies.Contains(assembly)
                ? mainApiOptions
                : slicerHostOptions;

            foreach (Type type in assembly.GetTypes())
            {
                if (!typeof(ControllerBase).IsAssignableFrom(type))
                {
                    continue;
                }

                string typeDisplayName = type.FullName ?? type.Name;

                CollectPolicyRoleGateOffenses(type, typeDisplayName, options, offenders);

                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.DeclaredOnly))
                {
                    string parameterSignature = string.Join(
                        ',',
                        method.GetParameters().Select(p => p.ParameterType.Name));
                    CollectPolicyRoleGateOffenses(
                        method,
                        $"{typeDisplayName}.{method.Name}({parameterSignature})",
                        options,
                        offenders);
                }
            }
        }

        offenders.Should().BeEmpty(
            "issue #1467 requires every API controller to gate access with " +
            "[RequirePermission(resource, action)] rather than a policy-based role alias such " +
            "as [Authorize(Policy = \"farm_admin\")] or [Authorize(Policy = \"RequireAdmin\")] " +
            "— these resolve to a RolesAuthorizationRequirement and are just as unreachable by " +
            "a custom role as a literal [Authorize(Roles = ...)] gate. Replace the policy-based " +
            "gate with [RequirePermission(resource, action)] on the offending member(s), or add " +
            "a documented, reasoned entry to AllowedPolicyRoleGates if a genuine exception " +
            $"applies. Offenders: {string.Join(", ", offenders)}");
    }

    private static void CollectPolicyRoleGateOffenses(
        MemberInfo member,
        string memberDisplayName,
        AuthorizationOptions options,
        List<string> offenders)
    {
        if (AllowedPolicyRoleGates.Contains(memberDisplayName))
        {
            return;
        }

        foreach (AuthorizeAttribute attribute in member.GetCustomAttributes<AuthorizeAttribute>(inherit: false))
        {
            if (string.IsNullOrWhiteSpace(attribute.Policy))
            {
                continue;
            }

            AuthorizationPolicy? policy = options.GetPolicy(attribute.Policy);
            if (policy is null)
            {
                continue;
            }

            if (policy.Requirements.Any(r => r is RolesAuthorizationRequirement))
            {
                offenders.Add($"{memberDisplayName} (policy: {attribute.Policy})");
                break;
            }
        }
    }

    private static AuthorizationOptions BuildMainApiAuthorizationOptions()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "0123456789abcdef0123456789abcdef",
                ["Jwt:Issuer"] = "PrintFarmer",
                ["Jwt:Audience"] = "PrintFarmer",
            })
            .Build();
        Mock<IWebHostEnvironment> environment = new();
        environment.SetupGet(e => e.EnvironmentName).Returns("Production");

        services.AddLogging();
        services.AddPrintFarmerAuthentication(configuration, environment.Object);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
    }

    private static AuthorizationOptions BuildSlicerHostAuthorizationOptions()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSlicerHostAuthorization();
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
    }

    /// <summary>
    /// Explicit, minimal allowlist for genuine exceptions. Each entry MUST carry a written
    /// reason as an inline comment explaining why a role-name gate (not a
    /// <c>[RequirePermission]</c> permission gate) is correct for that member. Entries are
    /// "Namespace.Type" for a type-level gate, or "Namespace.Type.Method(paramType1,paramType2)"
    /// for a method-level gate (parameter types are required to disambiguate overloads).
    /// </summary>
    private static readonly HashSet<string> AllowedRoleGates = new(StringComparer.Ordinal)
    {
        // Intentionally empty: issue #1451 removed every role-name gate from the API and
        // slicer host controllers. Add an entry here only with a written reason if a genuine
        // exception is ever needed (e.g. a gate that must remain role-based because it predates
        // or bootstraps the permission system itself), and keep the list as small as possible.
    };

    private static void CollectRoleGateOffenses(
        MemberInfo member,
        string memberDisplayName,
        List<string> offenders)
    {
        if (AllowedRoleGates.Contains(memberDisplayName))
        {
            return;
        }

        foreach (AuthorizeAttribute attribute in member.GetCustomAttributes<AuthorizeAttribute>(inherit: false))
        {
            if (!string.IsNullOrWhiteSpace(attribute.Roles))
            {
                offenders.Add(memberDisplayName);
                break;
            }
        }
    }
}
