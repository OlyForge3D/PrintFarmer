using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class DirectoryCreateResponse
{
    [JsonPropertyName("item")]
    public MoonrakerDirectoryInfo Item { get; set; } = new();

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;
}
