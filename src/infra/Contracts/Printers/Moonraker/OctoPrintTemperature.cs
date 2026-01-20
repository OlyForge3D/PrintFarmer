using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintTemperature
{
    [JsonPropertyName("actual")]
    public double Actual { get; set; }

    [JsonPropertyName("offset")]
    public double Offset { get; set; }

    [JsonPropertyName("target")]
    public double Target { get; set; }
}
