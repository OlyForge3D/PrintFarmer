using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Services.Printers;
using Farm.Modules.Gcode.Services.Gcode;

namespace Farm.Modules.Gcode.Tests.Services.Gcode;

public sealed class PrinterFilePreviewWireContractTests
{
    [Theory]
    [InlineData(PrinterFilePreviewState.Available, "Available")]
    [InlineData(PrinterFilePreviewState.Unavailable, "Unavailable")]
    [InlineData(PrinterFilePreviewState.Unsupported, "Unsupported")]
    [InlineData(PrinterFilePreviewState.Deferred, "Deferred")]
    public void Serialize_PreviewState_PreservesExactCamelCaseWireContract(
        PrinterFilePreviewState state, string expectedState)
    {
        Guid locationId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        DateTime capturedAtUtc = new(2026, 10, 26, 18, 0, 0, DateTimeKind.Utc);
        string thumbnailUrl = $"/api/printer-files/locations/{locationId}/thumbnail?observationRevision=7";
        PrinterFilePreviewDto dto = new(
            locationId, 7, capturedAtUtc, true, thumbnailUrl, 3600, 0.2, "image_unavailable", state);
        JsonSerializerOptions options = CreateWireOptions();

        string serialized = JsonSerializer.Serialize(dto, options);
        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement json = document.RootElement;
        Assert.Equal(
            [
                "locationId", "observationRevision", "capturedAtUtc", "hasImage", "thumbnailUrl",
                "printTimeSeconds", "layerHeightMm", "reasonCode", "state", "sizeBytes",
            ],
            json.EnumerateObject().Select(property => property.Name));
        Assert.Equal(locationId, json.GetProperty("locationId").GetGuid());
        Assert.Equal(7, json.GetProperty("observationRevision").GetInt64());
        Assert.Equal("2026-10-26T18:00:00Z", json.GetProperty("capturedAtUtc").GetString());
        Assert.True(json.GetProperty("hasImage").GetBoolean());
        Assert.Equal(thumbnailUrl, json.GetProperty("thumbnailUrl").GetString());
        Assert.Equal(3600, json.GetProperty("printTimeSeconds").GetDouble());
        Assert.Equal(0.2, json.GetProperty("layerHeightMm").GetDouble());
        Assert.Equal("image_unavailable", json.GetProperty("reasonCode").GetString());
        Assert.Equal(JsonValueKind.String, json.GetProperty("state").ValueKind);
        Assert.Equal(expectedState, json.GetProperty("state").GetString());
        Assert.Equal(dto, JsonSerializer.Deserialize<PrinterFilePreviewDto>(serialized, options));
    }

    [Fact]
    public void Serialize_UnavailableMeasurements_PreservesExplicitNullsAndOptionalProperties()
    {
        PrinterFilePreviewDto dto = new(
            Guid.Empty, 7, null, false, null, null, null, null, PrinterFilePreviewState.Unavailable);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(dto, CreateWireOptions()));
        JsonElement json = document.RootElement;
        Assert.Equal(
            [
                "locationId", "observationRevision", "capturedAtUtc", "hasImage",
                "printTimeSeconds", "layerHeightMm", "state", "sizeBytes",
            ],
            json.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("capturedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("printTimeSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("layerHeightMm").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("sizeBytes").ValueKind);
        Assert.False(json.GetProperty("hasImage").GetBoolean());
        Assert.Equal("Unavailable", json.GetProperty("state").GetString());
    }

    [Fact]
    public void Serialize_Metadata_UsesUnitSuffixedCamelCaseAndKeepsZeroFalseAndToolOrder()
    {
        PrinterPrintMetadata metadata = new()
        {
            BrimWidthMm = 0,
            QuietLeftPresent = false,
            FilamentUsedPerToolG = [12.5, 0],
            FilamentTypePerTool = ["PETG", "PLA"],
            NozzleDiameterMm = 0.4,
            FilamentCost = 0,
        };
        PrinterFilePreviewDto dto = new(
            Guid.Empty, 7, null, false, null, null, null, null, PrinterFilePreviewState.Available, 629505, metadata);
        JsonSerializerOptions options = CreateWireOptions();

        string serialized = JsonSerializer.Serialize(dto, options);
        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement json = document.RootElement;

        Assert.Equal(629505, json.GetProperty("sizeBytes").GetInt64());
        JsonElement wire = json.GetProperty("metadata");
        Assert.Equal(
            [
                "brimWidthMm", "filamentCost", "filamentUsedPerToolG", "filamentTypePerTool",
                "nozzleDiameterMm", "quietLeftPresent",
            ],
            wire.EnumerateObject().Select(property => property.Name));
        Assert.Equal(0, wire.GetProperty("brimWidthMm").GetDouble());
        Assert.Equal(0, wire.GetProperty("filamentCost").GetDouble());
        Assert.Equal(JsonValueKind.False, wire.GetProperty("quietLeftPresent").ValueKind);
        Assert.Equal([12.5, 0], wire.GetProperty("filamentUsedPerToolG").EnumerateArray().Select(x => x.GetDouble()));
        Assert.Equal(["PETG", "PLA"], wire.GetProperty("filamentTypePerTool").EnumerateArray().Select(x => x.GetString()));
        Assert.False(wire.TryGetProperty("hasValues", out _));
        Assert.Equal(serialized, JsonSerializer.Serialize(
            JsonSerializer.Deserialize<PrinterFilePreviewDto>(serialized, options), options));
    }

    [Fact]
    public void PreviewState_DeclaredValues_MatchFrontendContract()
    {
        Assert.Equal(
            ["Available", "Unavailable", "Unsupported", "Deferred"],
            Enum.GetNames<PrinterFilePreviewState>());
    }

    private static JsonSerializerOptions CreateWireOptions() =>
        // Match MVC property/null settings without a global enum converter: the enum owns its wire format.
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}
