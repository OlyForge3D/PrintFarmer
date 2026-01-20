using System.Text.Json.Serialization;

#pragma warning disable CA2227 // Collection properties should be read only

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class ObjectQueryResponse
{
    [JsonPropertyName("status")]
    public Dictionary<string, object> Status { get; set; } = new Dictionary<string, object>();

    [JsonPropertyName("eventtime")]
    public double EventTime { get; set; }
}

#pragma warning restore CA2227
