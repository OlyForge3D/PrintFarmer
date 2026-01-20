using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("color")]
    public string Color { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("default")]
    public bool Default { get; set; }

    [JsonPropertyName("current")]
    public bool Current { get; set; }

    [JsonPropertyName("heatedBed")]
    public bool HeatedBed { get; set; }

    [JsonPropertyName("heatedChamber")]
    public bool HeatedChamber { get; set; }
}
