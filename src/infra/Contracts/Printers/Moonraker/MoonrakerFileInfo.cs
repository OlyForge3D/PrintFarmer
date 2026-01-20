using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class MoonrakerFileInfo
{
    [JsonPropertyName("filename")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("modified")]
    public double Modified { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("permissions")]
    public string? Permissions { get; set; }
}
