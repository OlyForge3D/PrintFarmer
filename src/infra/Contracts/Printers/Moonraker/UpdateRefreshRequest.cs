using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class UpdateRefreshRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
