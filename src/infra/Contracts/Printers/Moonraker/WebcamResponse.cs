using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class WebcamResponse
{
    [JsonPropertyName("webcam")]
    public WebcamInfo Webcam { get; set; } = new();

    [JsonPropertyName("action")]
    public string? Action { get; set; }
}
