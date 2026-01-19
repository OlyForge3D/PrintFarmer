using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Printer Status Models
public class ObjectListResponse
{
    [JsonPropertyName("objects")]
    public string[] Objects { get; set; } = Array.Empty<string>();
}
