using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class AuthInfo
{
    [JsonPropertyName("default_source")]
    public string DefaultSource { get; set; } = string.Empty;

    [JsonPropertyName("available_sources")]
    public string[] AvailableSources { get; set; } = Array.Empty<string>();
}
