using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
/// <b>Scope, deliberately matching issue #1452's ask:</b> this checks
/// <see cref="AuthorizeAttribute.Roles"/> only, on REST controllers in the main API
/// (<c>Farm.Web.Api</c>) and slicer host (<c>Farm.Slicer.Module.Api</c>) assemblies. It does
/// <em>not</em> cover two related, pre-existing surfaces found during review, which are
/// deliberately left out of this test's scope rather than being silently swept into its
/// allowlist:
/// <list type="bullet">
/// <item>Policy-based role aliases such as <c>[Authorize(Policy = "farm_admin")]</c> or
/// <c>[Authorize(Policy = "RequireAdmin")]</c> (registered via <c>policy.RequireRole(...)</c> in
/// <c>AuthenticationStartup.cs</c>) are functionally equivalent role-name gates, but are a much
/// larger, pre-existing surface that #1451 did not migrate. Closing that gap needs its own
/// migration effort and is tracked as follow-up issue #1467, not folded into this test's
/// allowlist.</item>
/// <item>SignalR Hub methods (e.g. <c>HarvestHub</c>) are not <see cref="ControllerBase"/>
/// -derived and are out of scope for "API controllers" as the issue describes them.</item>
/// </list>
/// </summary>
public sealed class AuthorizeRolesGateArchitectureTests
{
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

    private static readonly Assembly[] ScannedAssemblies =
    [
        typeof(Farm.Web.Api.Controllers.PrintersController).Assembly,
        typeof(Farm.Slicer.Module.Api.Controllers.WorkersController).Assembly,
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
