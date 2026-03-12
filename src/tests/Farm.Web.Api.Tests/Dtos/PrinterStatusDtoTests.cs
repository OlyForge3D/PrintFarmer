using Farm.Infrastructure;

namespace Farm.Web.Api.Tests.Dtos;

public class PrinterStatusDtoTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("file.gcode", "file.gcode")]
    [InlineData(".cache/file.gcode", "file.gcode")]
    [InlineData("/deep/nested/path/file.gcode", "file.gcode")]
    [InlineData("folder/subfolder/print.gcode", "print.gcode")]
    public void ExtractFileName_ReturnsExpected(string? input, string? expected)
    {
        string? result = PrinterStatusDto.ExtractFileName(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WithNormalizedFileName_SetsFileNameFromJobName()
    {
        var dto = new PrinterStatusDto(
            Id: Guid.NewGuid(),
            IsOnline: true,
            State: "Printing",
            JobName: ".cache/side_skirt_x2_ASA.gcode");

        PrinterStatusDto normalized = dto.WithNormalizedFileName();

        Assert.Equal(".cache/side_skirt_x2_ASA.gcode", normalized.JobName);
        Assert.Equal("side_skirt_x2_ASA.gcode", normalized.FileName);
    }

    [Fact]
    public void WithNormalizedFileName_NoPathReturnsFileName()
    {
        var dto = new PrinterStatusDto(
            Id: Guid.NewGuid(),
            IsOnline: true,
            State: "Printing",
            JobName: "simple.gcode");

        PrinterStatusDto normalized = dto.WithNormalizedFileName();

        Assert.Equal("simple.gcode", normalized.JobName);
        Assert.Equal("simple.gcode", normalized.FileName);
    }

    [Fact]
    public void WithNormalizedFileName_NullJobNameClearsFileName()
    {
        var dto = new PrinterStatusDto(
            Id: Guid.NewGuid(),
            IsOnline: true,
            State: "Idle",
            JobName: null,
            FileName: "stale.gcode");

        PrinterStatusDto normalized = dto.WithNormalizedFileName();

        Assert.Null(normalized.JobName);
        Assert.Null(normalized.FileName);
    }

    [Fact]
    public void WithNormalizedFileName_EmptyJobNameClearsFileName()
    {
        var dto = new PrinterStatusDto(
            Id: Guid.NewGuid(),
            IsOnline: true,
            State: "Idle",
            JobName: "",
            FileName: "stale.gcode");

        PrinterStatusDto normalized = dto.WithNormalizedFileName();

        Assert.Equal("", normalized.JobName);
        Assert.Null(normalized.FileName);
    }

    [Fact]
    public void WithNormalizedFileName_NullJobNameNoStaleFileName_ReturnsSelf()
    {
        var dto = new PrinterStatusDto(
            Id: Guid.NewGuid(),
            IsOnline: false,
            State: null);

        PrinterStatusDto normalized = dto.WithNormalizedFileName();

        Assert.Same(dto, normalized);
    }
}
