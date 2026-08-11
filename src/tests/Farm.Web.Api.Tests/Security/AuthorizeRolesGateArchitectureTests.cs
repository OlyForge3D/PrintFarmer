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
/// <em>any</em> <see cref="AuthorizeAttribute.Roles"/> value set on an API controller type or
/// method, in the main API or the slicer host. A role-name gate is a surface a custom role can
/// never reach, no matter which permissions it holds, so new ones must not accumulate.
/// </summary>
public sealed class AuthorizeRolesGateArchitectureTests
{
    /// <summary>
    /// Explicit, minimal allowlist for genuine exceptions. Each entry MUST carry a written
    /// reason as an inline comment explaining why a role-name gate (not a
    /// <c>[RequirePermission]</c> permission gate) is correct for that member. Entries are
    /// "Namespace.Type" for a type-level gate, or "Namespace.Type.Method" for a method-level
    /// gate.
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
            foreach (Type type in assembly.GetTypes())
            {
                if (!IsApiController(type))
                {
                    continue;
                }

                string typeDisplayName = type.FullName ?? type.Name;

                CollectRoleGateOffenses(type, typeDisplayName, offenders);

                foreach (MethodInfo method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.DeclaredOnly))
                {
                    CollectRoleGateOffenses(method, $"{typeDisplayName}.{method.Name}", offenders);
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

    private static bool IsApiController(Type type)
    {
        if (type.IsAbstract || !type.IsPublic)
        {
            return false;
        }

        return typeof(ControllerBase).IsAssignableFrom(type);
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
