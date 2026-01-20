using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintLoginResponse
{
    [JsonPropertyName("_is_external_client")]
    public bool IsExternalClient { get; set; }

    [JsonPropertyName("_login_mechanism")]
    public string LoginMechanism { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("user")]
    public bool User { get; set; }

    [JsonPropertyName("admin")]
    public bool Admin { get; set; }

    [JsonPropertyName("apikey")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("permissions")]
    public string[] Permissions { get; set; } = Array.Empty<string>();

    [JsonPropertyName("groups")]
    public string[] Groups { get; set; } = Array.Empty<string>();
}
