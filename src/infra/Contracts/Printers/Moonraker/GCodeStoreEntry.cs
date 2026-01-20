using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class GCodeStoreEntry
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("time")]
    public double Time { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}
