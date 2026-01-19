using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintPrinterResponse
{
    [JsonPropertyName("temperature")]
    public Dictionary<string, OctoPrintTemperature> Temperature { get; set; } = new Dictionary<string, OctoPrintTemperature>();

    [JsonPropertyName("state")]
    public OctoPrintState State { get; set; } = new();
}

#pragma warning restore CA2227
