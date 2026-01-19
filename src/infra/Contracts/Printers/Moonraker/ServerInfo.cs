using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Server Administration Models
public class ServerInfo
{
    [JsonPropertyName("klippy_connected")]
    public bool KlippyConnected { get; set; }

    [JsonPropertyName("klippy_state")]
    public string KlippyState { get; set; } = string.Empty;

    [JsonPropertyName("components")]
    public string[] Components { get; set; } = Array.Empty<string>();

    [JsonPropertyName("failed_components")]
    public string[] FailedComponents { get; set; } = Array.Empty<string>();

    [JsonPropertyName("registered_directories")]
    public string[] RegisteredDirectories { get; set; } = Array.Empty<string>();

    [JsonPropertyName("warnings")]
    public string[] Warnings { get; set; } = Array.Empty<string>();

    [JsonPropertyName("websocket_count")]
    public int WebsocketCount { get; set; }

    [JsonPropertyName("moonraker_version")]
    public string MoonrakerVersion { get; set; } = string.Empty;

    [JsonPropertyName("api_version")]
    public int[] ApiVersion { get; set; } = Array.Empty<int>();

    [JsonPropertyName("api_version_string")]
    public string ApiVersionString { get; set; } = string.Empty;
}
