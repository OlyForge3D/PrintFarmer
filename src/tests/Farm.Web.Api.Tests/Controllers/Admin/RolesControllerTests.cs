using System.Reflection;
using System.Security.Claims;
using Farm.Infrastructure.Contracts.Roles;
using Farm.Infrastructure.Services.Roles;
using Farm.Web.Api.Controllers.Admin;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers.Admin;

/// <summary>
/// Issue #1448: every role-CRUD mutation must require <c>farm_admin</c>, and every
/// <see cref="RoleManagementException"/> error code must map to the HTTP status code that
/// reflects the violated invariant (404/403/409/400) rather than a single indistinguishable
/// code. A live-pipeline test is not possible in this unit project (no authentication handler
/// is wired), so the authorization contract is verified on the action metadata instead, and the
/// status-code mapping is verified by driving the controller directly with a mocked
/// <see cref="IRoleManagementService"/> (see <c>FilamentFallbackGroupsControllerAuthorizationTests</c>
/// for the precedent this follows).
/// </summary>
public sealed class RolesControllerTests
{
    private static readonly Type ControllerType = typeof(RolesController);
    private static readonly Guid ActorUserId = Guid.NewGuid();

    [Fact]
    public void Controller_RequiresFarmAdminRole_AtClassLevel()
    {
        AuthorizeAttribute? classAuthorize = ControllerType
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .FirstOrDefault();

        classAuthorize.Should().NotBeNull("every role-management endpoint, including reads, is admin-only");
        classAuthorize!.Roles.Should().Be("farm_admin",
            "a non-farm_admin authenticated user must receive 403 on every /api/admin/roles endpoint");
    }

    [Theory]
    [InlineData(nameof(RolesController.GetRolesAsync))]
    [InlineData(nameof(RolesController.GetRoleAsync))]
    [InlineData(nameof(RolesController.CreateRoleAsync))]
    [InlineData(nameof(RolesController.UpdateRoleAsync))]
    [InlineData(nameof(RolesController.DeleteRoleAsync))]
    public void Actions_DoNotOverrideOrWeakenTheClassLevelAuthorization(string methodName)
    {
        MethodInfo? method = ControllerType.GetMethod(methodName);
        method.Should().NotBeNull();

        method!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Should().BeEmpty(
                $"{methodName} must rely on the class-level [Authorize(Roles = \"farm_admin\")] gate, not a weaker per-action override");
    }

    [Theory]
    [InlineData(RoleManagementErrorCode.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(RoleManagementErrorCode.SystemRoleProtected, StatusCodes.Status403Forbidden)]
    [InlineData(RoleManagementErrorCode.HasMembers, StatusCodes.Status409Conflict)]
    [InlineData(RoleManagementErrorCode.LastAdminRole, StatusCodes.Status409Conflict)]
    [InlineData(RoleManagementErrorCode.SelfLockout, StatusCodes.Status409Conflict)]
    [InlineData(RoleManagementErrorCode.ConcurrencyConflict, StatusCodes.Status409Conflict)]
    [InlineData(RoleManagementErrorCode.InvalidName, StatusCodes.Status400BadRequest)]
    [InlineData(RoleManagementErrorCode.NameIsImmutable, StatusCodes.Status400BadRequest)]
    [InlineData(RoleManagementErrorCode.InvalidReassignmentTarget, StatusCodes.Status400BadRequest)]
    [InlineData(RoleManagementErrorCode.InvalidPermission, StatusCodes.Status400BadRequest)]
    public async Task DeleteRoleAsync_MapsEachErrorCodeToItsExpectedStatusCode(RoleManagementErrorCode errorCode, int expectedStatusCode)
    {
        Mock<IRoleManagementService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(s => s.DeleteRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RoleManagementException(errorCode, "test failure"));

        RolesController controller = CreateController(service.Object);

        IActionResult result = await controller.DeleteRoleAsync(Guid.NewGuid(), reassignTo: null, cascade: false, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>();
        ((ObjectResult)result).StatusCode.Should().Be(expectedStatusCode);
    }

    [Fact]
    public async Task DeleteRoleAsync_HasMembers_ReturnsStructuredRoleHasMembersResponseWithMemberCount()
    {
        // Issue #1448 documents RoleHasMembersResponse (with MemberCount) as the 409 body for
        // the "role still has members" case; the generic { error, code } payload used by other
        // error codes would break the documented contract for generated clients/UI code.
        Mock<IRoleManagementService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(s => s.DeleteRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RoleManagementException(RoleManagementErrorCode.HasMembers, "Role 'operators' has 3 member(s).") { MemberCount = 3 });

        RolesController controller = CreateController(service.Object);

        IActionResult result = await controller.DeleteRoleAsync(Guid.NewGuid(), reassignTo: null, cascade: false, CancellationToken.None);

        ObjectResult objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        RoleHasMembersResponse body = objectResult.Value.Should().BeOfType<RoleHasMembersResponse>().Which;
        body.MemberCount.Should().Be(3);
        body.Error.Should().Contain("operators");
    }

    [Fact]
    public async Task DeleteRoleAsync_MapsEachErrorCodeToItsExpectedStatusCode_MemberCountUnset()
    {
        // Every non-HasMembers error code (and a HasMembers thrown without MemberCount set, in
        // case a future call site forgets to populate it) must still fall back to the generic
        // { error, code } payload rather than throwing while trying to build a
        // RoleHasMembersResponse from a null count.
        Mock<IRoleManagementService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(s => s.DeleteRoleAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RoleManagementException(RoleManagementErrorCode.HasMembers, "test failure"));

        RolesController controller = CreateController(service.Object);

        IActionResult result = await controller.DeleteRoleAsync(Guid.NewGuid(), reassignTo: null, cascade: false, CancellationToken.None);

        ObjectResult objectResult = result.Should().BeOfType<ObjectResult>().Which;
        objectResult.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        objectResult.Value.Should().NotBeOfType<RoleHasMembersResponse>();
    }

    [Theory]
    [InlineData(RoleManagementErrorCode.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(RoleManagementErrorCode.SystemRoleProtected, StatusCodes.Status403Forbidden)]
    [InlineData(RoleManagementErrorCode.LastAdminRole, StatusCodes.Status409Conflict)]
    [InlineData(RoleManagementErrorCode.SelfLockout, StatusCodes.Status409Conflict)]
    [InlineData(RoleManagementErrorCode.ConcurrencyConflict, StatusCodes.Status409Conflict)]
    [InlineData(RoleManagementErrorCode.NameIsImmutable, StatusCodes.Status400BadRequest)]
    public async Task UpdateRoleAsync_MapsEachErrorCodeToItsExpectedStatusCode(RoleManagementErrorCode errorCode, int expectedStatusCode)
    {
        Mock<IRoleManagementService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(s => s.UpdateRoleAsync(It.IsAny<Guid>(), It.IsAny<UpdateCustomRoleRequest>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RoleManagementException(errorCode, "test failure"));

        RolesController controller = CreateController(service.Object);

        ActionResult<RoleDetailDto> result = await controller.UpdateRoleAsync(Guid.NewGuid(), new UpdateCustomRoleRequest(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>();
        ((ObjectResult)result.Result!).StatusCode.Should().Be(expectedStatusCode);
    }

    [Fact]
    public async Task DeleteRoleAsync_ReturnsUnauthorized_WhenActorClaimIsMissing()
    {
        Mock<IRoleManagementService> service = new(MockBehavior.Strict);
        RolesController controller = CreateController(service.Object, includeActorClaim: false);

        IActionResult result = await controller.DeleteRoleAsync(Guid.NewGuid(), reassignTo: null, cascade: false, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        service.VerifyNoOtherCalls();
    }

    private static RolesController CreateController(IRoleManagementService service, bool includeActorClaim = true)
    {
        RolesController controller = new(service);

        List<Claim> claims = [];
        if (includeActorClaim)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, ActorUserId.ToString()));
        }

        ClaimsIdentity identity = new(claims, authenticationType: includeActorClaim ? "Test" : null);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };

        return controller;
    }
}
