using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class ServerConfig
{
    [JsonPropertyName("config")]
    public Dictionary<string, object> Config { get; set; } = new Dictionary<string, object>();

    [JsonPropertyName("orig")]
    public Dictionary<string, object> Orig { get; set; } = new Dictionary<string, object>();

    [JsonPropertyName("files")]
    public ConfigFile[] Files { get; set; } = Array.Empty<ConfigFile>();
}

#pragma warning restore CA2227
