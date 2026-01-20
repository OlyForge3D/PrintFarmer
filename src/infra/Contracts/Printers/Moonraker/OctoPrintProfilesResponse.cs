using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintProfilesResponse
{
    [JsonPropertyName("profiles")]
    public Dictionary<string, OctoPrintProfile> Profiles { get; set; } = new Dictionary<string, OctoPrintProfile>();
}

#pragma warning restore CA2227
