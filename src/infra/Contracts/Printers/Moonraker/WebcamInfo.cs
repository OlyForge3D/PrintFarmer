using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only
#pragma warning disable CA1056 // URI-like properties should not be strings (JSON transport models)

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class WebcamInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("service")]
    public string Service { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    [JsonPropertyName("target_fps")]
    public int TargetFps { get; set; }

    [JsonPropertyName("target_fps_idle")]
    public int TargetFpsIdle { get; set; }

    [JsonPropertyName("stream_url")]
    public string StreamUrl { get; set; } = string.Empty;

    [JsonPropertyName("snapshot_url")]
    public string SnapshotUrl { get; set; } = string.Empty;

    [JsonPropertyName("flip_horizontal")]
    public bool FlipHorizontal { get; set; }

    [JsonPropertyName("flip_vertical")]
    public bool FlipVertical { get; set; }

    [JsonPropertyName("rotation")]
    public int Rotation { get; set; }

    [JsonPropertyName("aspect_ratio")]
    public string AspectRatio { get; set; } = string.Empty;

    [JsonPropertyName("extra_data")]
    public Dictionary<string, object> ExtraData { get; set; } = new Dictionary<string, object>();

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("uid")]
    public string Uid { get; set; } = string.Empty;
}

#pragma warning restore CA1056
#pragma warning restore CA2227
