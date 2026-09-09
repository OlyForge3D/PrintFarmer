using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Printers;

namespace Farm.Infrastructure.Tests.Dtos;

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

    [Fact]
    public void Serialize_SafetyTelemetry_UsesCamelCaseAndDistinctTemperatureFacts()
    {
        DateTime observedAtUtc =
            new(2026, 9, 9, 18, 0, 0, DateTimeKind.Utc);
        var dto = new PrinterStatusDto(
            Guid.NewGuid(),
            true,
            "Idle",
            SafetyTelemetry: new PrinterSafetyTelemetryDto(
                new SafetyScalarTelemetryFactDto(
                    210,
                    observedAtUtc,
                    15,
                    "measured"),
                new SafetyScalarTelemetryFactDto(
                    250,
                    observedAtUtc,
                    15,
                    "target"),
                new SafetyAxesTelemetryFactDto(
                    ["x", "y", "z"],
                    observedAtUtc,
                    15,
                    "homed"),
                new SafetyVectorTelemetryFactDto(
                    new SafetyVector3Dto(0, 0, 0),
                    observedAtUtc,
                    15,
                    "origin")));

        using JsonDocument json = JsonDocument.Parse(
            JsonSerializer.Serialize(
                dto,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        JsonElement safety = json.RootElement.GetProperty("safetyTelemetry");

        Assert.Equal(
            210,
            safety.GetProperty("measuredHotendTemperatureC")
                .GetProperty("value").GetDouble());
        Assert.Equal(
            250,
            safety.GetProperty("targetHotendTemperatureC")
                .GetProperty("value").GetDouble());
        Assert.Equal(
            15,
            safety.GetProperty("homedAxes")
                .GetProperty("staleAfterSeconds").GetInt32());
    }

    [Fact]
    public void Normalize_StatusWithoutSafetyFields_ProvidesRequiredFactObjects()
    {
        DateTime observedAtUtc =
            new(2026, 9, 9, 18, 0, 0, DateTimeKind.Utc);
        var status = new PrinterStatusDto(
            Guid.NewGuid(),
            IsOnline: false,
            State: "Offline");

        PrinterStatusDto normalized =
            PrinterSafetyTelemetryNormalizer.Normalize(
                status,
                existing: null,
                observedAtUtc);

        Assert.NotNull(normalized.SafetyTelemetry);
        Assert.Null(
            normalized.SafetyTelemetry.MeasuredHotendTemperatureC.Value);
        Assert.Null(
            normalized.SafetyTelemetry.TargetHotendTemperatureC.Value);
        Assert.Null(normalized.SafetyTelemetry.HomedAxes.Value);
        Assert.Null(
            normalized.SafetyTelemetry.CoordinateOriginOffsetMm.Value);
        Assert.Equal(
            15,
            normalized.SafetyTelemetry.MeasuredHotendTemperatureC
                .StaleAfterSeconds);
    }
}
