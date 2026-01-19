using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintFile
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
