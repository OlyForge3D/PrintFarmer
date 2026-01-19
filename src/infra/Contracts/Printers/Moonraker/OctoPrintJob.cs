using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintJob
{
    [JsonPropertyName("file")]
    public OctoPrintFile File { get; set; } = new();

    [JsonPropertyName("estimatedPrintTime")]
    public double? EstimatedPrintTime { get; set; }

    [JsonPropertyName("filament")]
    public OctoPrintFilament Filament { get; set; } = new();

    [JsonPropertyName("user")]
    public string? User { get; set; }
}
