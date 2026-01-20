using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class UpdateClientRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
