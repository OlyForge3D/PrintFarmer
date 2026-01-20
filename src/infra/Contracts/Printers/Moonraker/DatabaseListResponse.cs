using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Database Models
public class DatabaseListResponse
{
    [JsonPropertyName("namespaces")]
    public string[] Namespaces { get; set; } = Array.Empty<string>();

    [JsonPropertyName("backups")]
    public string[] Backups { get; set; } = Array.Empty<string>();
}
