using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class VersionParts
{
    [JsonPropertyName("major")]
    public string Major { get; set; } = string.Empty;

    [JsonPropertyName("minor")]
    public string Minor { get; set; } = string.Empty;

    [JsonPropertyName("build_number")]
    public string BuildNumber { get; set; } = string.Empty;
}
