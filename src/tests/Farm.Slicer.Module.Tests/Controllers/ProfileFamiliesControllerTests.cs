using System.Security.Claims;
using System.Text.Json;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

public sealed class ProfileFamiliesControllerTests
{
    [Fact]
    public async Task CloneFamilyAsync_SourcePresetUnavailable_PreservesWorkerFailureDetail()
    {
        Guid userId = Guid.NewGuid();
        const string detail =
            "Family 'Farm Test' profile 'Farm Test 0.6 nozzle' is missing parent 'Stock 0.6 nozzle'.";
        var request = new CloneProfileFamilyRequestDto();
        Mock<IProfileFamilyService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(candidate => candidate.CloneFamilyAsync(
                request,
                userId,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProfileFamilySourceException(detail));
        var controller = new ProfileFamiliesController(
            service.Object,
            NullLogger<ProfileFamiliesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                        "Test"))
                }
            }
        };

        IActionResult result = await controller.CloneFamilyAsync(
            request,
            CancellationToken.None);

        UnprocessableEntityObjectResult unprocessable =
            result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        JsonElement body = JsonSerializer.SerializeToElement(unprocessable.Value);
        body.GetProperty("code").GetString().Should().Be("source_preset_unavailable");
        body.GetProperty("detail").GetString().Should().Be(detail);
        body.TryGetProperty("message", out _).Should().BeFalse();
    }

    /// <summary>
    /// #2093: a hash-index violation raised on the edit path (RenderAndInstallAsync's
    /// DbUpdateException catch chain) must map to 409 profile_family_hash_conflict, matching the
    /// clone endpoint, rather than falling through to the generic 500 handler.
    /// </summary>
    [Fact]
    public async Task EditFamilyAsync_HashConflict_Returns409WithHashConflictCode()
    {
        Guid userId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        const string detail =
            "A slicer profile family with the same rendered content already exists: 'Existing Family'.";
        var request = new EditProfileFamilyRequestDto { Name = "Renamed" };
        Mock<IProfileFamilyService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(candidate => candidate.EditFamilyAsync(
                familyId,
                request,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProfileFamilyHashConflictException(detail));
        var controller = new ProfileFamiliesController(
            service.Object,
            NullLogger<ProfileFamiliesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                        "Test"))
                }
            }
        };

        IActionResult result = await controller.EditFamilyAsync(familyId, request, CancellationToken.None);

        ConflictObjectResult conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonElement body = JsonSerializer.SerializeToElement(conflict.Value);
        body.GetProperty("code").GetString().Should().Be("profile_family_hash_conflict");
        body.GetProperty("detail").GetString().Should().Be(detail);
    }

    /// <summary>
    /// #2093: same mapping as the edit endpoint, for the standalone re-render endpoint.
    /// </summary>
    [Fact]
    public async Task RenderFamilyAsync_HashConflict_Returns409WithHashConflictCode()
    {
        Guid userId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        const string detail =
            "A machine profile with the same rendered content already exists: 'Stock 0.6 nozzle'.";
        Mock<IProfileFamilyService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(candidate => candidate.RenderFamilyAsync(
                familyId,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProfileFamilyHashConflictException(detail));
        var controller = new ProfileFamiliesController(
            service.Object,
            NullLogger<ProfileFamiliesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                        "Test"))
                }
            }
        };

        IActionResult result = await controller.RenderFamilyAsync(familyId, CancellationToken.None);

        ConflictObjectResult conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonElement body = JsonSerializer.SerializeToElement(conflict.Value);
        body.GetProperty("code").GetString().Should().Be("profile_family_hash_conflict");
        body.GetProperty("detail").GetString().Should().Be(detail);
    }

    [Fact]
    public async Task CloneFamilyAsync_ConcurrencyConflict_Returns409WithCamelCaseEnvelope()
    {
        Guid userId = Guid.NewGuid();
        var request = new CloneProfileFamilyRequestDto();
        Mock<IProfileFamilyService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(candidate => candidate.CloneFamilyAsync(request, userId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProfileFamilyConcurrencyException("retry"));
        ProfileFamiliesController controller = CreateController(service, userId);

        IActionResult result = await controller.CloneFamilyAsync(request, CancellationToken.None);

        AssertError(result, StatusCodes.Status409Conflict, "profile_family_concurrent_modification", "retry");
    }

    [Fact]
    public async Task DeleteFamilyAsync_ConcurrencyConflict_Returns409WithCamelCaseEnvelope()
    {
        Guid userId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        Mock<IProfileFamilyService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(candidate => candidate.DeleteFamilyAsync(familyId, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProfileFamilyConcurrencyException("retry"));
        ProfileFamiliesController controller = CreateController(service, userId);

        IActionResult result = await controller.DeleteFamilyAsync(familyId, false, CancellationToken.None);

        AssertError(result, StatusCodes.Status409Conflict, "profile_family_concurrent_modification", "retry");
    }

    [Fact]
    public async Task EditFamilyAsync_ConcurrencyConflict_Returns409WithCamelCaseEnvelope()
    {
        Guid userId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        var request = new EditProfileFamilyRequestDto();
        Mock<IProfileFamilyService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(candidate => candidate.EditFamilyAsync(familyId, request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProfileFamilyConcurrencyException("retry"));
        ProfileFamiliesController controller = CreateController(service, userId);

        IActionResult result = await controller.EditFamilyAsync(familyId, request, CancellationToken.None);

        AssertError(result, StatusCodes.Status409Conflict, "profile_family_concurrent_modification", "retry");
    }

    [Fact]
    public async Task RenderFamilyAsync_ConcurrentDelete_Returns409WithCamelCaseEnvelope()
    {
        Guid userId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        Mock<IProfileFamilyService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(candidate => candidate.RenderFamilyAsync(familyId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProfileFamilyConcurrentlyDeletedException("deleted"));
        ProfileFamiliesController controller = CreateController(service, userId);

        IActionResult result = await controller.RenderFamilyAsync(familyId, CancellationToken.None);

        AssertError(result, StatusCodes.Status409Conflict, "profile_family_deleted_concurrently", "deleted");
    }

    [Fact]
    public async Task RenderFamilyAsync_CleanupFailure_Returns503WithCamelCaseEnvelope()
    {
        Guid userId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        Mock<IProfileFamilyService> service = new(MockBehavior.Strict);
        _ = service
            .Setup(candidate => candidate.RenderFamilyAsync(familyId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProfileFamilyCleanupException("cleanup failed"));
        ProfileFamiliesController controller = CreateController(service, userId);

        IActionResult result = await controller.RenderFamilyAsync(familyId, CancellationToken.None);

        AssertError(result, StatusCodes.Status503ServiceUnavailable, "profile_family_cleanup_failed", "cleanup failed");
    }

    private static ProfileFamiliesController CreateController(
        Mock<IProfileFamilyService> service,
        Guid userId) =>
        new(service.Object, NullLogger<ProfileFamiliesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                        "Test"))
                }
            }
        };

    private static void AssertError(
        IActionResult result,
        int expectedStatus,
        string expectedCode,
        string expectedDetail)
    {
        ObjectResult error = result.Should().BeAssignableTo<ObjectResult>().Subject;
        error.StatusCode.Should().Be(expectedStatus);
        JsonElement body = JsonSerializer.SerializeToElement(error.Value);
        body.GetProperty("code").GetString().Should().Be(expectedCode);
        body.GetProperty("detail").GetString().Should().Be(expectedDetail);
        body.TryGetProperty("message", out _).Should().BeFalse();
    }
}
