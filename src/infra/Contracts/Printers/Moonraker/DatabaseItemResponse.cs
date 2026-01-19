using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class DatabaseItemResponse
{
    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}
