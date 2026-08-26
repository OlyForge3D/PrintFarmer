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
        body.GetProperty("message").GetString().Should().Be(detail);
    }
}
