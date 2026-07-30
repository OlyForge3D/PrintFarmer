using System.Linq;
using System.Reflection;
using Farm.Web.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Issue #711 round-5 FIX 4: fallback-group configuration mutations must require the
/// <c>farm_admin</c> role (matching <c>PrintersController</c>/<c>MaintenanceController</c>), while
/// read endpoints remain reachable by any authenticated user. The role metadata asserted here is
/// exactly what the ASP.NET Core authorization middleware uses to return 403 for non-admins on
/// mutations and 200 for authenticated reads. A live-pipeline test is not possible in this unit
/// project (no authentication handler is wired), so the authorization contract is verified on the
/// action metadata instead.
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
    public void MutationEndpoints_RequireFarmAdminRole(string methodName)
    {
        MethodInfo? method = ControllerType.GetMethod(methodName);
        method.Should().NotBeNull();

        AuthorizeAttribute? authorize = method!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .FirstOrDefault();

        authorize.Should().NotBeNull($"{methodName} mutates fallback configuration and must require farm_admin");
        authorize!.Roles.Should().Be("farm_admin",
            "a non-farm_admin authenticated user must receive 403 on fallback-group mutations");
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
