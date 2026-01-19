using System.Text.Json.Serialization;

#pragma warning disable CA1056 // URI-like properties should not be strings (JSON transport models)

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class AgentInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

#pragma warning restore CA1056
