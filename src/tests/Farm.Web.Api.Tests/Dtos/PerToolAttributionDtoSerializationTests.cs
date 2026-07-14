using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Json;

namespace Farm.Web.Api.Tests.Dtos;

/// <summary>
/// Golden-shape wire-contract tests for the per-tool attribution surface added to
/// <see cref="PrinterDetailsDto"/> and <see cref="ToolheadDto"/> (issue #711, F6 backend).
///
/// <para>The React SPA (#719) reads two additive fields from the details endpoint:</para>
/// <list type="bullet">
///   <item><c>supportsPerToolAttribution</c> on the printer envelope — a plain bool
///     that is always emitted; <c>true</c> only when the operator feature is on AND
///     the printer domain flag is set.</item>
///   <item><c>cumulativePrintHours</c> on each toolhead — numeric (including <c>0</c>)
///     when attribution is active, explicit <c>null</c> otherwise. The value is
///     always emitted because the global serializer has
///     <see cref="JsonIgnoreCondition.WhenWritingNull"/> and the DTO overrides it.</item>
/// </list>
/// </summary>
public sealed class PerToolAttributionDtoSerializationTests
{
    /// <summary>
    /// Mirrors <c>ControllerStartup.AddPrintFarmerControllers</c> so the JSON shape
    /// asserted here matches what the API emits on the wire.
    /// </summary>
    private static JsonSerializerOptions CreateApiSerializerOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new PrinterBackendJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static PrinterDetailsDto CreateDetailsDto(
        bool supportsPerToolAttribution,
        params ToolheadDto[] toolheads)
    {
        return new PrinterDetailsDto(
            Id: Guid.NewGuid(),
            Name: "wire-shape-test-printer",
            ServerUrl: "http://wire.local",
            Notes: null,
            ManufacturerId: null,
            ManufacturerName: null,
            ModelId: null,
            ModelName: null,
            ModelMotionType: null,
            ModelMaxX: null,
            ModelMaxY: null,
            ModelMaxZ: null,
            DateAcquired: null,
            Toolheads: toolheads,
            SupportsPerToolAttribution: supportsPerToolAttribution);
    }

    private static ToolheadDto CreateToolheadDto(int index, double? cumulativeHours, bool isPrimary = false)
    {
        return new ToolheadDto(
            Id: Guid.NewGuid(),
            Name: $"T{index}",
            Index: index,
            NozzleDiameter: null,
            NozzleType: null,
            MaxFlowRate: null,
            MaxTemp: null,
            HotendModelId: null,
            HotendModelName: null,
            ExtruderModelId: null,
            ExtruderModelName: null,
            ToolheadModelDefId: null,
            ToolheadModelDefName: null,
            NozzleModelId: null,
            NozzleModelName: null,
            SupportedMaterials: null,
            IsPrimary: isPrimary,
            CumulativePrintHours: cumulativeHours);
    }

    [Fact]
    public void PrinterDetailsDto_FeatureEnabledSupportedPrinterWithHours_SerializesTrueAndNumeric()
    {
        PrinterDetailsDto dto = CreateDetailsDto(
            supportsPerToolAttribution: true,
            CreateToolheadDto(index: 0, cumulativeHours: 12.5, isPrimary: true),
            CreateToolheadDto(index: 1, cumulativeHours: 3.25));

        string json = JsonSerializer.Serialize(dto, CreateApiSerializerOptions());

        json.Should().Contain("\"supportsPerToolAttribution\":true");
        json.Should().Contain("\"cumulativePrintHours\":12.5");
        json.Should().Contain("\"cumulativePrintHours\":3.25");
        json.Should().NotContain("\"cumulativePrintHours\":null", "positive-hour scenario should have numeric values only");
    }

    [Fact]
    public void PrinterDetailsDto_FeatureEnabledUnsupportedPrinter_SerializesFalseAndNulls()
    {
        // Feature-enabled but printer has SupportsPerToolAttribution == false, so the
        // controller collapses both the flag and the odometers to their unset values.
        PrinterDetailsDto dto = CreateDetailsDto(
            supportsPerToolAttribution: false,
            CreateToolheadDto(index: 0, cumulativeHours: null, isPrimary: true));

        string json = JsonSerializer.Serialize(dto, CreateApiSerializerOptions());

        json.Should().Contain("\"supportsPerToolAttribution\":false");
        json.Should().Contain("\"cumulativePrintHours\":null",
            "unsupported printers must emit explicit null so consumers see 'not applicable'");
    }

    [Fact]
    public void PrinterDetailsDto_FeatureGloballyDisabled_SerializesFalseAndNulls()
    {
        // Feature is globally off (operator has disabled it), so even a domain-supported
        // printer projects both fields to their unset values.
        PrinterDetailsDto dto = CreateDetailsDto(
            supportsPerToolAttribution: false,
            CreateToolheadDto(index: 0, cumulativeHours: null, isPrimary: true),
            CreateToolheadDto(index: 1, cumulativeHours: null));

        string json = JsonSerializer.Serialize(dto, CreateApiSerializerOptions());

        json.Should().Contain("\"supportsPerToolAttribution\":false");
        // Both toolheads must render an explicit null cumulativePrintHours entry.
        Regex.Matches(json, "\"cumulativePrintHours\":null").Should().HaveCount(2);
    }

    [Fact]
    public void ToolheadDto_ZeroCumulativeHours_SerializesAsNumericZeroNotNull()
    {
        // Regression guard for the "supported printer, fresh baseline" case where
        // CumulativePrintHours == 0.0 must serialize as a numeric zero. The UI needs
        // this to distinguish "supported and zero" from "not applicable".
        ToolheadDto toolhead = CreateToolheadDto(index: 0, cumulativeHours: 0.0, isPrimary: true);

        string json = JsonSerializer.Serialize(toolhead, CreateApiSerializerOptions());

        json.Should().Contain("\"cumulativePrintHours\":0");
        json.Should().NotContain("\"cumulativePrintHours\":null");
    }

    [Fact]
    public void ToolheadDto_NullCumulativeHours_SerializesAsExplicitNullDespiteGlobalWhenWritingNull()
    {
        // The global serializer has DefaultIgnoreCondition = WhenWritingNull. The DTO
        // opts out via [property: JsonIgnore(Condition = Never)] so gated-off consumers
        // still see the field with an explicit null value rather than a missing key.
        ToolheadDto toolhead = CreateToolheadDto(index: 0, cumulativeHours: null);

        string json = JsonSerializer.Serialize(toolhead, CreateApiSerializerOptions());

        json.Should().Contain("\"cumulativePrintHours\":null");
    }

    [Fact]
    public void PrinterDetailsDto_LegacyJsonMissingNewField_DeserializesWithDefaultFalse()
    {
        // A producer running an older build (before this change) never emits
        // supportsPerToolAttribution. New readers must still deserialize successfully
        // and default the flag to false.
        string legacyJson = """
            {
              "id": "00000000-0000-0000-0000-000000000001",
              "name": "legacy",
              "serverUrl": "http://legacy.local",
              "backend": "Moonraker"
            }
            """;

        PrinterDetailsDto? decoded = JsonSerializer.Deserialize<PrinterDetailsDto>(
            legacyJson,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new PrinterBackendJsonConverter(), new JsonStringEnumConverter() },
            });

        decoded.Should().NotBeNull();
        decoded!.SupportsPerToolAttribution.Should().BeFalse();
        decoded.FallbackGroups.Should().BeNull("legacy JSON never emitted this either");
    }

    [Fact]
    public void ToolheadDto_LegacyJsonMissingNewField_DeserializesWithDefaultNull()
    {
        // Same compatibility guarantee at the toolhead level: legacy producers omit
        // cumulativePrintHours; new readers must default it to null.
        string legacyJson = """
            {
              "id": "00000000-0000-0000-0000-000000000010",
              "name": "T0",
              "index": 0,
              "isPrimary": true
            }
            """;

        ToolheadDto? decoded = JsonSerializer.Deserialize<ToolheadDto>(
            legacyJson,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() },
            });

        decoded.Should().NotBeNull();
        decoded!.CumulativePrintHours.Should().BeNull();
        decoded.Index.Should().Be(0);
        decoded.IsPrimary.Should().BeTrue();
    }
}
