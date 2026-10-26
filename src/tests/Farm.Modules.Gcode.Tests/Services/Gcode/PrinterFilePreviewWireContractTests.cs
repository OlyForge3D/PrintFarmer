using System.Text.Json;
using System.Text.Json.Serialization;
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
                "printTimeSeconds", "layerHeightMm", "reasonCode", "state",
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
                "printTimeSeconds", "layerHeightMm", "state",
            ],
            json.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("capturedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("printTimeSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("layerHeightMm").ValueKind);
        Assert.False(json.GetProperty("hasImage").GetBoolean());
        Assert.Equal("Unavailable", json.GetProperty("state").GetString());
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
