using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain.Sync;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Services.Sync;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Controller tests for <see cref="LibrarySyncController"/> verifying claims extraction,
/// status-code mapping for domain exceptions (400/403/409), delegation to the service, and
/// camelCase + string-enum serialization of the 409 conflict body.
/// </summary>
public class LibrarySyncControllerTests
{
    private readonly Mock<ILibrarySyncService> _serviceMock = new();

    private LibrarySyncController CreateController(Guid userId, bool isAdmin)
    {
        var controller = new LibrarySyncController(_serviceMock.Object);
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

    private static LibrarySyncController CreateAnonymousController(ILibrarySyncService service)
    {
        var controller = new LibrarySyncController(service);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };
        return controller;
    }

    [Fact]
    public async Task Pull_ReturnsOkWithResult()
    {
        Guid userId = Guid.NewGuid();
        var expected = new LibrarySyncPullResultDto { HasMore = false, ServerRevision = 3 };
        _ = _serviceMock
            .Setup(s => s.PullAsync(null, null, userId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        LibrarySyncController controller = CreateController(userId, isAdmin: false);

        ActionResult<LibrarySyncPullResultDto> result = await controller.PullChangesAsync(null, null, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
    }

    [Fact]
    public async Task Pull_PassesAdminFlagFromRole()
    {
        Guid userId = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.PullAsync(It.IsAny<string?>(), It.IsAny<int?>(), userId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LibrarySyncPullResultDto());
        LibrarySyncController controller = CreateController(userId, isAdmin: true);

        _ = await controller.PullChangesAsync(null, null, CancellationToken.None);

        _serviceMock.Verify(s => s.PullAsync(null, null, userId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pull_MalformedCursor_Returns400()
    {
        Guid userId = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.PullAsync("bad", null, userId, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidSyncCursorException("bad cursor"));
        LibrarySyncController controller = CreateController(userId, isAdmin: false);

        ActionResult<LibrarySyncPullResultDto> result = await controller.PullChangesAsync("bad", null, CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Pull_NoUserId_Returns401()
    {
        LibrarySyncController controller = CreateAnonymousController(_serviceMock.Object);

        ActionResult<LibrarySyncPullResultDto> result = await controller.PullChangesAsync(null, null, CancellationToken.None);

        _ = Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task Apply_ReturnsOkWithResult()
    {
        Guid userId = Guid.NewGuid();
        var request = new ApplySyncRequestDto();
        var expected = new ApplySyncResultDto { ServerRevision = 7 };
        _ = _serviceMock
            .Setup(s => s.ApplyAsync(request, userId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        LibrarySyncController controller = CreateController(userId, isAdmin: false);

        ActionResult<ApplySyncResultDto> result = await controller.ApplyAsync(request, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expected, ok.Value);
    }

    [Fact]
    public async Task Apply_Conflict_Returns409WithConflictBody()
    {
        Guid userId = Guid.NewGuid();
        Guid entityId = Guid.NewGuid();
        var conflicts = new List<SyncConflictDto>
        {
            new()
            {
                EntityType = SyncEntityType.ModelCollection,
                EntityId = entityId,
                Reason = "stale",
                Server = new SyncConflictVersionDto { Exists = true, Revision = 5 },
                Submitted = new SyncConflictVersionDto { Revision = 4 }
            }
        };
        _ = _serviceMock
            .Setup(s => s.ApplyAsync(It.IsAny<ApplySyncRequestDto>(), userId, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SyncConflictException(conflicts, 5));
        LibrarySyncController controller = CreateController(userId, isAdmin: false);

        ActionResult<ApplySyncResultDto> result = await controller.ApplyAsync(new ApplySyncRequestDto(), CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        SyncConflictResponseDto body = Assert.IsType<SyncConflictResponseDto>(conflict.Value);
        Assert.Equal(5, body.ServerRevision);
        _ = Assert.Single(body.Conflicts);
        Assert.Equal(entityId, body.Conflicts[0].EntityId);
    }

    [Fact]
    public async Task Apply_ConflictBody_SerializesCamelCaseWithStringEnums()
    {
        Guid entityId = Guid.NewGuid();
        var body = new SyncConflictResponseDto
        {
            Error = "conflict",
            ServerRevision = 9,
            Conflicts =
            [
                new SyncConflictDto
                {
                    EntityType = SyncEntityType.ModelCollectionMembership,
                    EntityId = entityId,
                    Reason = "stale",
                    Server = new SyncConflictVersionDto { Exists = true, Revision = 2, IsShared = true },
                    Submitted = new SyncConflictVersionDto { Revision = 1 }
                }
            ]
        };

        // Mirror the API's configured serializer (camelCase + string enums).
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());

        string json = JsonSerializer.Serialize(body, options);

        Assert.Contains("\"serverRevision\":9", json, StringComparison.Ordinal);
        Assert.Contains("\"conflicts\":", json, StringComparison.Ordinal);
        Assert.Contains("\"entityType\":\"ModelCollectionMembership\"", json, StringComparison.Ordinal);
        Assert.Contains("\"isShared\":true", json, StringComparison.Ordinal);
        // No PascalCase leakage.
        Assert.DoesNotContain("\"ServerRevision\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"EntityType\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_AccessDenied_Returns403()
    {
        Guid userId = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.ApplyAsync(It.IsAny<ApplySyncRequestDto>(), userId, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CollectionAccessDeniedException());
        LibrarySyncController controller = CreateController(userId, isAdmin: false);

        ActionResult<ApplySyncResultDto> result = await controller.ApplyAsync(new ApplySyncRequestDto(), CancellationToken.None);

        ObjectResult obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    [Fact]
    public async Task Apply_InvalidModel_Returns400()
    {
        Guid userId = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.ApplyAsync(It.IsAny<ApplySyncRequestDto>(), userId, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CollectionModelValidationException([Guid.NewGuid()]));
        LibrarySyncController controller = CreateController(userId, isAdmin: false);

        ActionResult<ApplySyncResultDto> result = await controller.ApplyAsync(new ApplySyncRequestDto(), CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Apply_ArgumentException_Returns400()
    {
        Guid userId = Guid.NewGuid();
        _ = _serviceMock
            .Setup(s => s.ApplyAsync(It.IsAny<ApplySyncRequestDto>(), userId, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("bad batch"));
        LibrarySyncController controller = CreateController(userId, isAdmin: false);

        ActionResult<ApplySyncResultDto> result = await controller.ApplyAsync(new ApplySyncRequestDto(), CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Apply_NoUserId_Returns401()
    {
        LibrarySyncController controller = CreateAnonymousController(_serviceMock.Object);

        ActionResult<ApplySyncResultDto> result = await controller.ApplyAsync(new ApplySyncRequestDto(), CancellationToken.None);

        _ = Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }
}
