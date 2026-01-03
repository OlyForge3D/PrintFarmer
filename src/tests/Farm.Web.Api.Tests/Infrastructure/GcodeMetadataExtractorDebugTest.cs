using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Telemetry;
using FluentAssertions;
using Moq;

namespace Farm.Web.Api.Tests.Infrastructure;

public class GcodeMetadataExtractorDebugTest
{
    [Fact]
    public async Task DebugEndOfFileExtraction()
    {
        var loggerMock = new Mock<IUnifiedLoggingService>();
        var service = new GcodeMetadataExtractorService(loggerMock.Object);

        // Create a simple gcode with metadata at the end
        var lines = new List<string>();

        // Add 600 lines of gcode
        for (int i = 0; i < 600; i++)
        {
            lines.Add($"G1 X{i} Y{i}");
        }

        // Add metadata in the last 500 lines (line 601)
        lines.Add("; layer_height = 0.2");
        lines.Add("; nozzle_diameter = 0.4");
        lines.Add("; filament_type = PLA");

        string gcodeContent = string.Join("\n", lines);

        var result = await service.ExtractMetadataAsync(gcodeContent);

        // These should be parsed from the end
        result.LayerHeight.Should().Be(0.2);
        result.NozzleDiameter.Should().Be(0.4);
        result.Material.Should().Be("PLA");
    }
}
