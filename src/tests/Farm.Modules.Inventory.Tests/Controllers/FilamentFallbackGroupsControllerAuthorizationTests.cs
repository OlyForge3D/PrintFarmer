using System.Linq;
using System.Reflection;
using Farm.Infrastructure.Authorization;
using Farm.Modules.Inventory.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Farm.Modules.Inventory.Tests.Controllers;

/// <summary>
/// Issue #711 round-5 FIX 4 (updated for issue #1451): fallback-group configuration mutations
/// must require the <c>filament_type:admin</c> permission (matching the migrated
/// <c>PrintersController</c>/<c>MaintenanceController</c> sites), while read endpoints remain
/// reachable by any authenticated user. farm_admin still reaches these mutations exactly as
/// before, via the unconditional bypass plus the seeded <c>admin</c> grant on every resource — a
/// custom role can now reach them too, by holding <c>filament_type:admin</c> without being named
/// farm_admin. A live-pipeline test is not possible in this unit project (no authentication
/// handler is wired), so the authorization contract is verified on the action metadata instead.
/// </summary>
public sealed class FilamentFallbackGroupsControllerAuthorizationTests
{
    private static readonly System.Type ControllerType = typeof(FilamentFallbackGroupsController);

    [Fact]
    public void Controller_RequiresAuthentication_WithoutRoleRestrictionAtClassLevel()
    {
        AuthorizeAttribute? classAuthorize = ControllerType
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .FirstOrDefault();

        classAuthorize.Should().NotBeNull("every endpoint requires an authenticated user");
        classAuthorize!.Roles.Should().BeNullOrEmpty(
            "read endpoints must be reachable by any authenticated user, so the class-level gate carries no role");
    }

    [Theory]
    [InlineData(nameof(FilamentFallbackGroupsController.CreateAsync))]
    [InlineData(nameof(FilamentFallbackGroupsController.UpdateAsync))]
    [InlineData(nameof(FilamentFallbackGroupsController.DeleteAsync))]
    public void MutationEndpoints_RequireFilamentTypeAdminPermission(string methodName)
    {
        MethodInfo? method = ControllerType.GetMethod(methodName);
        method.Should().NotBeNull();

        RequirePermissionAttribute? requirePermission = method!
            .GetCustomAttributes<RequirePermissionAttribute>(inherit: true)
            .FirstOrDefault();

        requirePermission.Should().NotBeNull(
            $"{methodName} mutates fallback configuration and must require filament_type:admin");
        requirePermission!.Resource.Should().Be("filament_type");
        requirePermission.Action.Should().Be("admin");
    }

    [Theory]
    [InlineData(nameof(FilamentFallbackGroupsController.ListAsync))]
    [InlineData(nameof(FilamentFallbackGroupsController.GetAsync))]
    [InlineData(nameof(FilamentFallbackGroupsController.GetAvailableFallbackAsync))]
    public void ReadEndpoints_DoNotAddRoleRestriction(string methodName)
    {
        MethodInfo? method = ControllerType.GetMethod(methodName);
        method.Should().NotBeNull();

        method!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Should().BeEmpty(
                "read endpoints inherit the class-level [Authorize] and must stay reachable by any authenticated user (200, not 403)");
    }
}
