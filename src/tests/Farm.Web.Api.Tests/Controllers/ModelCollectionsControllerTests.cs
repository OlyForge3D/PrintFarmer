using System.Security.Claims;
using Farm.Api.Controllers;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Collections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class ModelCollectionsControllerTests
{
    private readonly Mock<IModelCollectionService> _serviceMock = new();
    private readonly ModelCollectionsController _controller;
    private readonly Guid _userId = Guid.NewGuid();

    public ModelCollectionsControllerTests()
    {
        _controller = new ModelCollectionsController(NullLogger<ModelCollectionsController>.Instance, _serviceMock.Object);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
            new Claim(ClaimTypes.Role, "Admin")
        }, "TestAuth");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private static ModelCollectionDto SampleCollection(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "Fleet",
        Visibility = ModelCollectionVisibility.Private
    };

    [Fact]
    public async Task ListAsync_ReturnsOkWithCollections()
    {
        IReadOnlyList<ModelCollectionDto> data = new List<ModelCollectionDto> { SampleCollection() };
        _serviceMock.Setup(s => s.ListAsync(It.IsAny<CollectionCaller>(), It.IsAny<CancellationToken>())).ReturnsAsync(data);

        ActionResult<IEnumerable<ModelCollectionDto>> result = await _controller.ListAsync(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(data, ok.Value);
    }

    [Fact]
    public async Task ListAsync_PassesCallerWithUserIdAndAdminRole()
    {
        CollectionCaller captured = default;
        _serviceMock.Setup(s => s.ListAsync(It.IsAny<CollectionCaller>(), It.IsAny<CancellationToken>()))
            .Callback<CollectionCaller, CancellationToken>((c, _) => captured = c)
            .ReturnsAsync(new List<ModelCollectionDto>());

        _ = await _controller.ListAsync(CancellationToken.None);

        Assert.Equal(_userId, captured.UserId);
        Assert.True(captured.IsAdmin);
    }

    [Fact]
    public async Task GetAsync_ReturnsOk()
    {
        ModelCollectionDto dto = SampleCollection();
        _serviceMock.Setup(s => s.GetAsync(It.IsAny<CollectionCaller>(), dto.Id, It.IsAny<CancellationToken>())).ReturnsAsync(dto);

        ActionResult<ModelCollectionDto> result = await _controller.GetAsync(dto.Id, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(dto, ok.Value);
    }

    [Fact]
    public async Task CreateAsync_EmptyName_ReturnsBadRequest()
    {
        ActionResult<ModelCollectionDto> result = await _controller.CreateAsync(new CreateModelCollectionDto { Name = " " }, CancellationToken.None);

        var obj = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
    }

    [Fact]
    public async Task CreateAsync_Valid_ReturnsCreated()
    {
        ModelCollectionDto dto = SampleCollection();
        _serviceMock.Setup(s => s.CreateAsync(It.IsAny<CollectionCaller>(), It.IsAny<CreateModelCollectionDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(dto);

        ActionResult<ModelCollectionDto> result = await _controller.CreateAsync(new CreateModelCollectionDto { Name = "Fleet" }, CancellationToken.None);

        CreatedAtActionResult created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Same(dto, created.Value);
    }

    [Fact]
    public async Task UpdateAsync_EmptyName_ReturnsBadRequest()
    {
        ActionResult<ModelCollectionDto> result = await _controller.UpdateAsync(Guid.NewGuid(), new UpdateModelCollectionDto { Name = "" }, CancellationToken.None);

        var obj = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
    }

    [Fact]
    public async Task DeleteAsync_ReturnsNoContent()
    {
        IActionResult result = await _controller.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task ShareAsync_ReturnsOk()
    {
        ModelCollectionDto dto = SampleCollection();
        dto.Visibility = ModelCollectionVisibility.Shared;
        _serviceMock.Setup(s => s.ShareAsync(It.IsAny<CollectionCaller>(), dto.Id, It.IsAny<CancellationToken>())).ReturnsAsync(dto);

        ActionResult<ModelCollectionDto> result = await _controller.ShareAsync(dto.Id, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(dto, ok.Value);
    }

    [Fact]
    public async Task UnshareAsync_ReturnsOk()
    {
        ModelCollectionDto dto = SampleCollection();
        _serviceMock.Setup(s => s.UnshareAsync(It.IsAny<CollectionCaller>(), dto.Id, It.IsAny<CancellationToken>())).ReturnsAsync(dto);

        ActionResult<ModelCollectionDto> result = await _controller.UnshareAsync(dto.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddMemberAsync_EmptyModelId_ReturnsBadRequest()
    {
        ActionResult<ModelCollectionMembershipDto> result = await _controller.AddMemberAsync(Guid.NewGuid(), new AddModelCollectionMemberDto { ModelId = Guid.Empty }, CancellationToken.None);

        var obj = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
    }

    [Fact]
    public async Task AddMemberAsync_Valid_ReturnsOk()
    {
        Guid collectionId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        var membership = new ModelCollectionMembershipDto { Id = Guid.NewGuid(), CollectionId = collectionId, ModelId = modelId };
        _serviceMock.Setup(s => s.AddMemberAsync(It.IsAny<CollectionCaller>(), collectionId, modelId, It.IsAny<CancellationToken>())).ReturnsAsync(membership);

        ActionResult<ModelCollectionMembershipDto> result = await _controller.AddMemberAsync(collectionId, new AddModelCollectionMemberDto { ModelId = modelId }, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(membership, ok.Value);
    }

    [Fact]
    public async Task RemoveMemberAsync_ReturnsNoContent()
    {
        IActionResult result = await _controller.RemoveMemberAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task ReplaceMembersAsync_ReturnsOk()
    {
        Guid collectionId = Guid.NewGuid();
        IReadOnlyList<ModelCollectionMembershipDto> members = new List<ModelCollectionMembershipDto>();
        _serviceMock.Setup(s => s.ReplaceMembersAsync(It.IsAny<CollectionCaller>(), collectionId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(members);

        ActionResult<IEnumerable<ModelCollectionMembershipDto>> result = await _controller.ReplaceMembersAsync(collectionId, new ReplaceModelCollectionMembersDto { ModelIds = [Guid.NewGuid()] }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }
}

