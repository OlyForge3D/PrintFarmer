using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// GCode API Models
public class GCodeRequest
{
    [JsonPropertyName("script")]
    public string Script { get; set; } = string.Empty;
}
