using System.Text.Json.Serialization;

#pragma warning disable CA1056 // URI-like properties should not be strings (JSON transport models)

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintWebcam
{
    [JsonPropertyName("flipH")]
    public bool FlipH { get; set; }

    [JsonPropertyName("flipV")]
    public bool FlipV { get; set; }

    [JsonPropertyName("rotate90")]
    public bool Rotate90 { get; set; }

    [JsonPropertyName("streamUrl")]
    public string StreamUrl { get; set; } = string.Empty;

    [JsonPropertyName("webcamEnabled")]
    public bool WebcamEnabled { get; set; }
}

#pragma warning restore CA1056
