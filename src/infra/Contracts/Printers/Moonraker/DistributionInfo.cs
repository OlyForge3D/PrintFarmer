using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class DistributionInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("version_parts")]
    public VersionParts VersionParts { get; set; } = new();

    [JsonPropertyName("like")]
    public string Like { get; set; } = string.Empty;

    [JsonPropertyName("codename")]
    public string Codename { get; set; } = string.Empty;
}
