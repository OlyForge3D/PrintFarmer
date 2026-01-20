using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintCommandRequest
{
    [JsonPropertyName("commands")]
    public string[] Commands { get; set; } = Array.Empty<string>();
}
