using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class ExtensionCallRequest
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public Dictionary<string, object>? Params { get; set; }
}

#pragma warning restore CA2227
