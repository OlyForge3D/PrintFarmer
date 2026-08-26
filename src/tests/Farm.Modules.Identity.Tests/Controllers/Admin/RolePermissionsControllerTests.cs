using System.Security.Claims;
using Farm.Infrastructure.Dtos;
using Farm.Web.Api.Controllers.Admin;
using Farm.Web.Api.Services.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers.Admin;

/// <summary>
/// Controller tests for <see cref="RolePermissionsController"/> verifying claims
/// extraction and status-code mapping for every <see cref="RolePermissionUpdateResult"/> case.
/// </summary>
public class RolePermissionsControllerTests
{
    private readonly Mock<IRolePermissionService> _serviceMock = new();

    private RolePermissionsController CreateController(Guid? actingUserId)
    {
        var controller = new RolePermissionsController(_serviceMock.Object);
        var claims = new List<Claim>();
        if (actingUserId is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, actingUserId.Value.ToString()));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth", ClaimTypes.NameIdentifier, ClaimTypes.Role);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    private static RolePermissionsDto CreateSampleRoleDto(Guid roleId) => new()
    {
        RoleId = roleId,
        RoleName = "operators",
        RoleDisplayName = "Operators",
        IsSystemRole = false,
        IsEditable = true,
        HasImplicitTotalAccess = false,
        UpdatedAt = DateTime.UtcNow,
        Resources = [],
    };

    [Fact]
    public async Task GetPermissionsAsync_RoleExists_ReturnsOkWithDto()
    {
        Guid roleId = Guid.NewGuid();
        RolePermissionsDto dto = CreateSampleRoleDto(roleId);
        _ = _serviceMock
            .Setup(s => s.GetRolePermissionsAsync(roleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);
        RolePermissionsController controller = CreateController(Guid.NewGuid());

        ActionResult<RolePermissionsDto> result = await controller.GetPermissionsAsync(roleId, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(dto, ok.Value);
    }

    [Fact]
    public async Task GetPermissionsAsync_RoleMissing_ReturnsNotFound()
    {
        Guid roleId = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.GetRolePermissionsAsync(roleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RolePermissionsDto?)null);
        RolePermissionsController controller = CreateController(Guid.NewGuid());

        ActionResult<RolePermissionsDto> result = await controller.GetPermissionsAsync(roleId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePermissionsAsync_MissingActingUserClaim_ReturnsUnauthorized()
    {
        RolePermissionsController controller = CreateController(actingUserId: null);
        var body = new UpdateRolePermissionsRequestDto { UpdatedAt = DateTime.UtcNow, Permissions = [] };

        ActionResult<UpdateRolePermissionsResponseDto> result = await controller.UpdatePermissionsAsync(Guid.NewGuid(), body, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePermissionsAsync_Success_ReturnsOkWithResponse()
    {
        Guid roleId = Guid.NewGuid();
        Guid actingUserId = Guid.NewGuid();
        var response = new UpdateRolePermissionsResponseDto
        {
            Role = CreateSampleRoleDto(roleId),
            RevokedSessionCount = 0,
        };
        var body = new UpdateRolePermissionsRequestDto { UpdatedAt = DateTime.UtcNow, Permissions = ["queue:read"] };
        _ = _serviceMock
            .Setup(s => s.UpdateRolePermissionsAsync(roleId, body, actingUserId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RolePermissionUpdateResult.Success(response));
        RolePermissionsController controller = CreateController(actingUserId);

        ActionResult<UpdateRolePermissionsResponseDto> result = await controller.UpdatePermissionsAsync(roleId, body, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(response, ok.Value);
    }

    [Fact]
    public async Task UpdatePermissionsAsync_RoleNotFound_ReturnsNotFound()
    {
        Guid roleId = Guid.NewGuid();
        Guid actingUserId = Guid.NewGuid();
        var body = new UpdateRolePermissionsRequestDto { UpdatedAt = DateTime.UtcNow, Permissions = [] };
        _ = _serviceMock
            .Setup(s => s.UpdateRolePermissionsAsync(roleId, body, actingUserId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RolePermissionUpdateResult.RoleNotFound());
        RolePermissionsController controller = CreateController(actingUserId);

        ActionResult<UpdateRolePermissionsResponseDto> result = await controller.UpdatePermissionsAsync(roleId, body, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePermissionsAsync_FarmAdminImmutable_ReturnsBadRequest()
    {
        Guid roleId = Guid.NewGuid();
        Guid actingUserId = Guid.NewGuid();
        var body = new UpdateRolePermissionsRequestDto { UpdatedAt = DateTime.UtcNow, Permissions = [] };
        _ = _serviceMock
            .Setup(s => s.UpdateRolePermissionsAsync(roleId, body, actingUserId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RolePermissionUpdateResult.FarmAdminImmutable());
        RolePermissionsController controller = CreateController(actingUserId);

        ActionResult<UpdateRolePermissionsResponseDto> result = await controller.UpdatePermissionsAsync(roleId, body, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePermissionsAsync_InvalidPermissions_ReturnsBadRequestWithPermissionList()
    {
        Guid roleId = Guid.NewGuid();
        Guid actingUserId = Guid.NewGuid();
        var body = new UpdateRolePermissionsRequestDto { UpdatedAt = DateTime.UtcNow, Permissions = ["not:real"] };
        _ = _serviceMock
            .Setup(s => s.UpdateRolePermissionsAsync(roleId, body, actingUserId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RolePermissionUpdateResult.InvalidPermissions(["not:real"]));
        RolePermissionsController controller = CreateController(actingUserId);

        ActionResult<UpdateRolePermissionsResponseDto> result = await controller.UpdatePermissionsAsync(roleId, body, CancellationToken.None);

        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task UpdatePermissionsAsync_ConcurrencyConflict_ReturnsConflict()
    {
        Guid roleId = Guid.NewGuid();
        Guid actingUserId = Guid.NewGuid();
        var body = new UpdateRolePermissionsRequestDto { UpdatedAt = DateTime.UtcNow, Permissions = [] };
        _ = _serviceMock
            .Setup(s => s.UpdateRolePermissionsAsync(roleId, body, actingUserId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RolePermissionUpdateResult.ConcurrencyConflict());
        RolePermissionsController controller = CreateController(actingUserId);

        ActionResult<UpdateRolePermissionsResponseDto> result = await controller.UpdatePermissionsAsync(roleId, body, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdatePermissionsAsync_LockoutViolation_ReturnsConflictWithPermissionList()
    {
        Guid roleId = Guid.NewGuid();
        Guid actingUserId = Guid.NewGuid();
        var body = new UpdateRolePermissionsRequestDto { UpdatedAt = DateTime.UtcNow, Permissions = [] };
        _ = _serviceMock
            .Setup(s => s.UpdateRolePermissionsAsync(roleId, body, actingUserId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RolePermissionUpdateResult.LockoutViolation(["roles:admin"]));
        RolePermissionsController controller = CreateController(actingUserId);

        ActionResult<UpdateRolePermissionsResponseDto> result = await controller.UpdatePermissionsAsync(roleId, body, CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.NotNull(conflict.Value);
    }
}
