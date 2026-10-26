using Farm.Infrastructure;
using Farm.Infrastructure.Services.Gcode;
using Farm.Modules.Gcode.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Modules.Gcode.Tests.Controllers;

public class GcodeHarvestDiagnosticsControllerTests
{
    [Theory]
    [InlineData("part.gcode")]
    [InlineData("part.bgcode")]
    [InlineData("part.BGCODE")]
    public async Task AnalyzeGcodeAsync_SupportedExtension_ReturnsExtractedMetadata(string filename)
    {
        var expected = new GcodeMetadataDto { SlicerName = "PrusaSlicer" };
        var harvest = new Mock<IGcodeHarvestService>(MockBehavior.Strict);
        harvest.Setup(s => s.ExtractMetadataAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new GcodeHarvestDiagnosticsController(
            NullLogger<GcodeHarvestDiagnosticsController>.Instance, harvest.Object);
        using var stream = new MemoryStream("GCDE"u8.ToArray());
        var file = new FormFile(stream, 0, stream.Length, "file", filename);

        ActionResult<GcodeMetadataDto> response = await controller.AnalyzeGcodeAsync(file, CancellationToken.None);

        Assert.Equal(expected, Assert.IsType<OkObjectResult>(response.Result).Value);
        harvest.Verify(s => s.ExtractMetadataAsync(It.IsAny<Stream>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task AnalyzeGcodeAsync_MalformedBinary_ReturnsBadRequestWithSafeReason()
    {
        var harvest = new Mock<IGcodeHarvestService>();
        harvest.Setup(s => s.ExtractMetadataAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BinaryGcodeFormatException("checksum_mismatch"));
        var controller = new GcodeHarvestDiagnosticsController(
            NullLogger<GcodeHarvestDiagnosticsController>.Instance, harvest.Object);
        using var stream = new MemoryStream("GCDE"u8.ToArray());
        var file = new FormFile(stream, 0, stream.Length, "file", "part.bgcode");

        ActionResult<GcodeMetadataDto> response = await controller.AnalyzeGcodeAsync(file, CancellationToken.None);

        Assert.Equal("Cannot analyze binary G-code: checksum_mismatch",
            Assert.IsType<BadRequestObjectResult>(response.Result).Value);
    }
}
