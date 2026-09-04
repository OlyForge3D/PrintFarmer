using System.Security.Claims;
using Farm.Modules.Calibration.Services.Calibration;
using Farm.Modules.Calibration.Services.Gcode;
using Farm.Modules.Gcode.Controllers;
using Farm.Modules.Gcode.Services.Gcode;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Farm.Modules.Gcode.Tests.Controllers;

/// <summary>Tests the public explicit slice-save contract.</summary>
public sealed class GcodePromotionsSliceArtifactControllerTests
{
    [Fact]
    public async Task PromoteSliceArtifactAsync_WhenCreated_ReturnsContractWithoutStoragePaths()
    {
        Guid userId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        Guid artifactId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        var promoter = new Mock<IGcodeArtifactPromoter>();
        var library = new Mock<ISliceArtifactLibraryService>();
        library
            .Setup(service => service.PromoteAsync(
                jobId,
                artifactId,
                It.Is<CalibrationActor>(actor => actor.UserId == userId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CalibrationApiResult<SliceArtifactLibraryResult>.Success(
                new SliceArtifactLibraryResult
                {
                    GcodeFileId = gcodeFileId,
                    Name = "output.gcode",
                    SizeBytes = 42,
                    CreatedNew = true,
                    Printable = true,
                    SliceJobId = jobId,
                    SourceArtifactId = artifactId,
                },
                StatusCodes.Status201Created));
        var controller = new GcodePromotionsController(promoter.Object, library.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
                        "TestAuth")),
                },
            },
        };

        IActionResult result = await controller.PromoteSliceArtifactAsync(
            new SliceArtifactPromotionRequest
            {
                SliceJobId = jobId,
                ArtifactId = artifactId,
            },
            CancellationToken.None);

        SliceArtifactLibraryResult response = result.Should().BeOfType<ObjectResult>()
            .Which.Value.Should().BeOfType<SliceArtifactLibraryResult>().Subject;
        response.GcodeFileId.Should().Be(gcodeFileId);
        response.Name.Should().Be("output.gcode");
        response.CreatedNew.Should().BeTrue();
        response.GetType().GetProperties().Select(property => property.Name)
            .Should().BeEquivalentTo(
                [
                    "GcodeFileId",
                    "Name",
                    "SizeBytes",
                    "CreatedNew",
                    "Printable",
                    "SliceJobId",
                    "SourceArtifactId",
                ]);
    }
}
