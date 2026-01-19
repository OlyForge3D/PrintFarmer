using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintFeature
{
    [JsonPropertyName("sdSupport")]
    public bool SdSupport { get; set; }

    [JsonPropertyName("temperatureGraph")]
    public bool TemperatureGraph { get; set; }
}
