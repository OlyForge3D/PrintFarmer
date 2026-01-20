using System.Text.Json.Serialization;

#pragma warning disable CA1056 // URI-like properties should not be strings (JSON transport models)

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class WebcamTestResponse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("stream_url")]
    public string StreamUrl { get; set; } = string.Empty;

    [JsonPropertyName("snapshot_url")]
    public string SnapshotUrl { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

#pragma warning restore CA1056
