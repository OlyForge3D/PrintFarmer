using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class FileUploadResponse
{
    [JsonPropertyName("item")]
    public MoonrakerFileInfo Item { get; set; } = new();

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("upload_info")]
    public UploadInfo? UploadInfo { get; set; }
}
