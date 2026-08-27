using System.Security.Claims;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Services.Collections;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Controller tests for <see cref="ModelCollectionsController"/> verifying claims
/// extraction, status-code mapping for domain exceptions, and delegation to the service.
/// </summary>
public class ModelCollectionsControllerTests
{
    private readonly Mock<IModelCollectionService> _serviceMock = new();

    private ModelCollectionsController CreateController(Guid userId, bool isAdmin)
    {
        var controller = new ModelCollectionsController(_serviceMock.Object);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "farm_admin"));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth", ClaimTypes.NameIdentifier, ClaimTypes.Role);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static ModelCollectionsController CreateAnonymousController(IModelCollectionService service)
    {
        var controller = new ModelCollectionsController(service);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };
        return controller;
    }

    [Fact]
    public async Task List_ReturnsOkWithCollections()
    {
        Guid userId = Guid.NewGuid();
        var expected = new List<ModelCollectionDto> { new() { Id = Guid.NewGuid(), Name = "C" } };
        _ = _serviceMock
            .Setup(s => s.ListCollectionsAsync(userId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        ActionResult<IReadOnlyList<ModelCollectionDto>> result = await controller.ListAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
    }

    [Fact]
    public async Task List_PassesAdminFlagFromRole()
    {
        Guid userId = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.ListCollectionsAsync(userId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ModelCollectionDto>());
        ModelCollectionsController controller = CreateController(userId, isAdmin: true);

        _ = await controller.ListAsync(CancellationToken.None);

        _serviceMock.Verify(s => s.ListCollectionsAsync(userId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_WithoutUserId_ReturnsUnauthorized()
    {
        ModelCollectionsController controller = CreateAnonymousController(_serviceMock.Object);

        ActionResult<IReadOnlyList<ModelCollectionDto>> result = await controller.ListAsync(CancellationToken.None);

        _ = Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task Get_NotFound_Returns404()
    {
        Guid userId = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.GetCollectionAsync(id, userId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelCollectionDto?)null);
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        ActionResult<ModelCollectionDto> result = await controller.GetAsync(id, CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Get_AccessDenied_Returns403()
    {
        Guid userId = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.GetCollectionAsync(id, userId, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CollectionAccessDeniedException());
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        ActionResult<ModelCollectionDto> result = await controller.GetAsync(id, CancellationToken.None);

        ObjectResult obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    [Fact]
    public async Task Create_ReturnsCreated()
    {
        Guid userId = Guid.NewGuid();
        var dto = new CreateModelCollectionDto { Name = "New" };
        var created = new ModelCollectionDto { Id = Guid.NewGuid(), Name = "New", OwnerUserId = userId };
        _ = _serviceMock
            .Setup(s => s.CreateCollectionAsync(dto, userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        ActionResult<ModelCollectionDto> result = await controller.CreateAsync(dto, CancellationToken.None);

        CreatedAtActionResult createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Same(created, createdResult.Value);
    }

    [Fact]
    public async Task Create_InvalidArgument_Returns400()
    {
        Guid userId = Guid.NewGuid();
        var dto = new CreateModelCollectionDto { Name = "" };
        _ = _serviceMock
            .Setup(s => s.CreateCollectionAsync(dto, userId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Collection name is required"));
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        ActionResult<ModelCollectionDto> result = await controller.CreateAsync(dto, CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Delete_Success_Returns204()
    {
        Guid userId = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.DeleteCollectionAsync(id, userId, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        IActionResult result = await controller.DeleteAsync(id, CancellationToken.None);

        _ = Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        Guid userId = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.DeleteCollectionAsync(id, userId, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CollectionNotFoundException(id));
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        IActionResult result = await controller.DeleteAsync(id, CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task Share_DelegatesWithSharedTrue()
    {
        Guid userId = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.SetSharedAsync(id, true, userId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelCollectionDto { Id = id, IsShared = true });
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        ActionResult<ModelCollectionDto> result = await controller.ShareAsync(id, CancellationToken.None);

        _ = Assert.IsType<OkObjectResult>(result.Result);
        _serviceMock.Verify(s => s.SetSharedAsync(id, true, userId, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unshare_DelegatesWithSharedFalse()
    {
        Guid userId = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.SetSharedAsync(id, false, userId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelCollectionDto { Id = id, IsShared = false });
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        _ = await controller.UnshareAsync(id, CancellationToken.None);

        _serviceMock.Verify(s => s.SetSharedAsync(id, false, userId, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddMember_InvalidModel_Returns400()
    {
        Guid userId = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.AddMemberAsync(id, modelId, userId, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CollectionModelValidationException(new[] { modelId }));
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        ActionResult<ModelCollectionMembershipDto> result = await controller.AddMemberAsync(
            id, new AddModelCollectionMemberDto { ModelId = modelId }, CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddMember_EmptyModelId_Returns400WithoutServiceCall()
    {
        Guid userId = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        ActionResult<ModelCollectionMembershipDto> result = await controller.AddMemberAsync(
            id, new AddModelCollectionMemberDto { ModelId = Guid.Empty }, CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result.Result);
        _serviceMock.Verify(
            s => s.AddMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReplaceMembers_AccessDenied_Returns403()
    {
        Guid userId = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.ReplaceMembersAsync(id, It.IsAny<IEnumerable<Guid>>(), userId, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CollectionAccessDeniedException());
        ModelCollectionsController controller = CreateController(userId, isAdmin: false);

        ActionResult<ModelCollectionDto> result = await controller.ReplaceMembersAsync(
            id, new ReplaceModelCollectionMembersDto { ModelIds = new[] { Guid.NewGuid() } }, CancellationToken.None);

        ObjectResult obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }
}
