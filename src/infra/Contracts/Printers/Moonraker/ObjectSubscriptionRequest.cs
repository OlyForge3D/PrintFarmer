using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class ObjectSubscriptionRequest
{
    [JsonPropertyName("objects")]
    public Dictionary<string, string[]?> Objects { get; set; } = new Dictionary<string, string[]?>();
}

#pragma warning restore CA2227
