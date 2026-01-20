using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintSettingsResponse
{
    [JsonPropertyName("plugins")]
    public Dictionary<string, object> Plugins { get; set; } = new Dictionary<string, object>();

    [JsonPropertyName("feature")]
    public OctoPrintFeature Feature { get; set; } = new();

    [JsonPropertyName("webcam")]
    public OctoPrintWebcam Webcam { get; set; } = new();
}

#pragma warning restore CA2227
