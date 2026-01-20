using System.Text.Json.Serialization;

#pragma warning disable CA1056 // URI-like properties should not be strings (JSON transport models)

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class WebcamRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("target_fps")]
    public int? TargetFps { get; set; }

    [JsonPropertyName("target_fps_idle")]
    public int? TargetFpsIdle { get; set; }

    [JsonPropertyName("stream_url")]
    public string? StreamUrl { get; set; }

    [JsonPropertyName("snapshot_url")]
    public string? SnapshotUrl { get; set; }

    [JsonPropertyName("flip_horizontal")]
    public bool? FlipHorizontal { get; set; }

    [JsonPropertyName("flip_vertical")]
    public bool? FlipVertical { get; set; }

    [JsonPropertyName("rotation")]
    public int? Rotation { get; set; }

    [JsonPropertyName("aspect_ratio")]
    public string? AspectRatio { get; set; }
}

#pragma warning restore CA1056
