using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class ServiceRequest
{
    [JsonPropertyName("service")]
    public string Service { get; set; } = string.Empty;
}
