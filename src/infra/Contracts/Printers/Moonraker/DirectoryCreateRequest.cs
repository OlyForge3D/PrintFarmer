using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class DirectoryCreateRequest
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}
