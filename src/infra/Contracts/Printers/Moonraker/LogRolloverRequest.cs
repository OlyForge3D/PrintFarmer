using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class LogRolloverRequest
{
    [JsonPropertyName("application")]
    public string? Application { get; set; }
}
