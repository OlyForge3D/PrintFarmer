using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Api.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Issue #2203: <see cref="ProfilesController.DeleteCustomProfileAsync"/> exists to let a
/// non-admin caller delete their own custom profile without <c>slicer_engines:admin</c>, the
/// same way <see cref="ProfilesController.PromoteCalibrationDraftProfileAsync"/> (#2180) and
/// <see cref="ProfilesController.UpdateCustomProfileAsync"/> (#2189) already avoid it. Mirrors
/// <c>ProfilesControllerPromoteCalibrationDraftProfileTests</c> to prove, via reflection rather
/// than by reading the source, that the new endpoint is gated by
/// <see cref="PrintFarmerPermissions.Calibration.Update"/> rather than <c>slicer_engines:admin</c>
/// - a regression here would silently reintroduce the farm_admin requirement this endpoint exists
/// to remove - and that the admin-only <see cref="ProfilesController.DeleteProfileAsync"/> and
/// <see cref="ProfilesController.BulkDeleteProfilesAsync"/> routes remain unaffected.
/// </summary>
public sealed class ProfilesControllerDeleteCustomProfileTests
{
    [Fact]
    public void DeleteCustomProfileAsync_IsGatedByCalibrationUpdate_NotAdmin()
    {
        MethodInfo method = typeof(ProfilesController).GetMethod(nameof(ProfilesController.DeleteCustomProfileAsync))
            ?? throw new InvalidOperationException($"{nameof(ProfilesController.DeleteCustomProfileAsync)} not found via reflection");

        RequirePermissionAttribute methodAttribute = method.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()
            ?? throw new InvalidOperationException("Expected exactly one method-level RequirePermissionAttribute");

        Assert.Equal(PrintFarmerPermissions.Calibration.Update, methodAttribute.Permission);
        Assert.NotEqual("slicer_engines:admin", methodAttribute.Permission);
    }

    [Fact]
    public void DeleteProfileAsync_And_BulkDeleteProfilesAsync_RemainAdminGated()
    {
        // Guards against a future edit that relaxes the existing admin-only single-delete or
        // bulk-delete routes as a side effect of adding the new owner-scoped route - the two must
        // stay independent.
        MethodInfo deleteMethod = typeof(ProfilesController).GetMethod(nameof(ProfilesController.DeleteProfileAsync))
            ?? throw new InvalidOperationException($"{nameof(ProfilesController.DeleteProfileAsync)} not found via reflection");
        MethodInfo bulkDeleteMethod = typeof(ProfilesController).GetMethod(nameof(ProfilesController.BulkDeleteProfilesAsync))
            ?? throw new InvalidOperationException($"{nameof(ProfilesController.BulkDeleteProfilesAsync)} not found via reflection");

        RequirePermissionAttribute deleteAttribute = deleteMethod.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()
            ?? throw new InvalidOperationException("Expected exactly one method-level RequirePermissionAttribute on DeleteProfileAsync");
        RequirePermissionAttribute bulkDeleteAttribute = bulkDeleteMethod.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()
            ?? throw new InvalidOperationException("Expected exactly one method-level RequirePermissionAttribute on BulkDeleteProfilesAsync");

        Assert.Equal("slicer_engines:admin", deleteAttribute.Permission);
        Assert.Equal("slicer_engines:admin", bulkDeleteAttribute.Permission);
    }

    [Fact]
    public void DeleteCustomProfileAsync_DoesNotRequireInteractiveSession()
    {
        // Deliberate: this endpoint must be callable with a Desktop API-key exchange token (that's
        // the whole point of #2203 - PrintFarmerDesktop's calibration wizard calls it), which
        // InteractiveSessionRequirement would reject outright. Unlike its PUT sibling
        // UpdateCustomProfileAsync (which requires an interactive session and is web-only), this
        // route intentionally omits that policy. Codified as a test so a future "let's be
        // consistent with Update" edit doesn't silently break the desktop flow.
        MethodInfo method = typeof(ProfilesController).GetMethod(nameof(ProfilesController.DeleteCustomProfileAsync))
            ?? throw new InvalidOperationException($"{nameof(ProfilesController.DeleteCustomProfileAsync)} not found via reflection");

        Assert.Empty(method.GetCustomAttributes<AuthorizeAttribute>());
    }

    private static ProfilesController CreateController(IProfilesService profilesService)
    {
        ILogger<ProfilesController> logger = NullLogger<ProfilesController>.Instance;
        Mock<ICatalogServiceAdapter> catalogService = new(MockBehavior.Strict);
        ProfilesController controller = new(logger, profilesService, catalogService.Object);

        // GetCurrentUserId() reads User.FindFirst(ClaimTypes.NameIdentifier), which needs a
        // populated HttpContext.User - without it the action throws a NullReferenceException
        // before ever reaching IProfilesService, which every test below would otherwise
        // misreport as an unrelated 500.
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
            authenticationType: "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal },
        };
        return controller;
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_ReturnsNoContent_OnSuccess()
    {
        Mock<IProfilesService> service = new(MockBehavior.Strict);
        _ = service.Setup(s => s.DeleteCustomProfileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        ProfilesController controller = CreateController(service.Object);

        IActionResult result = await controller.DeleteCustomProfileAsync(Guid.NewGuid(), CancellationToken.None);

        _ = Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_ReturnsNotFound_WhenServiceThrowsKeyNotFound()
    {
        Mock<IProfilesService> service = new(MockBehavior.Strict);
        _ = service.Setup(s => s.DeleteCustomProfileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("not found"));

        ProfilesController controller = CreateController(service.Object);

        IActionResult result = await controller.DeleteCustomProfileAsync(Guid.NewGuid(), CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_ReturnsForbid_WhenServiceThrowsUnauthorizedAccess()
    {
        // The issue's explicit requirement: an ownership mismatch must be 403, never 404 - so a
        // caller can't distinguish "not mine" from "doesn't exist" via the wrong status code being
        // swapped in by a future edit.
        Mock<IProfilesService> service = new(MockBehavior.Strict);
        _ = service.Setup(s => s.DeleteCustomProfileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("not yours"));

        ProfilesController controller = CreateController(service.Object);

        IActionResult result = await controller.DeleteCustomProfileAsync(Guid.NewGuid(), CancellationToken.None);

        _ = Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_ReturnsBadRequest_WhenServiceThrowsInvalidOperation()
    {
        Mock<IProfilesService> service = new(MockBehavior.Strict);
        _ = service.Setup(s => s.DeleteCustomProfileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Cannot delete a system profile."));

        ProfilesController controller = CreateController(service.Object);

        IActionResult result = await controller.DeleteCustomProfileAsync(Guid.NewGuid(), CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task DeleteCustomProfileAsync_Returns500_OnUnexpectedException()
    {
        Mock<IProfilesService> service = new(MockBehavior.Strict);
        _ = service.Setup(s => s.DeleteCustomProfileAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidCastException("unexpected"));

        ProfilesController controller = CreateController(service.Object);

        IActionResult result = await controller.DeleteCustomProfileAsync(Guid.NewGuid(), CancellationToken.None);

        ObjectResult objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, objectResult.StatusCode);
    }
}
