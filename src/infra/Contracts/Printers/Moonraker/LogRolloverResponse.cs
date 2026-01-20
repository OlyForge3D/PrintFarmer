using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class LogRolloverResponse
{
    [JsonPropertyName("rolled_over")]
    public string[] RolledOver { get; set; } = Array.Empty<string>();

    [JsonPropertyName("failed")]
    public Dictionary<string, string> Failed { get; set; } = new Dictionary<string, string>();
}

#pragma warning restore CA2227
