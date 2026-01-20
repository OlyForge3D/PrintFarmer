using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class GCodeStoreResponse
{
    [JsonPropertyName("gcode_store")]
    public GCodeStoreEntry[] GCodeStore { get; set; } = Array.Empty<GCodeStoreEntry>();
}
