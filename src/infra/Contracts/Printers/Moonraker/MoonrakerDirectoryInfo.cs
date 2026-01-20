using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class MoonrakerDirectoryInfo
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("dirname")]
    public string Dirname { get; set; } = string.Empty;

    [JsonPropertyName("modified")]
    public double Modified { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("permissions")]
    public string? Permissions { get; set; }

    [JsonPropertyName("dirs")]
    public MoonrakerDirectoryInfo[] Dirs { get; set; } = Array.Empty<MoonrakerDirectoryInfo>();

    [JsonPropertyName("files")]
    public MoonrakerFileInfo[] Files { get; set; } = Array.Empty<MoonrakerFileInfo>();

    [JsonPropertyName("disk_usage")]
    public DiskUsage? DiskUsage { get; set; }
}
