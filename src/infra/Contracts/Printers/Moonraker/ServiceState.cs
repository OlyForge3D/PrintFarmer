using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class ServiceState
{
    [JsonPropertyName("active_state")]
    public string ActiveState { get; set; } = string.Empty;

    [JsonPropertyName("sub_state")]
    public string SubState { get; set; } = string.Empty;
}
