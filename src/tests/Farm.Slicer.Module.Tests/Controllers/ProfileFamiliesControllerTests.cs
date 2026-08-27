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
}
