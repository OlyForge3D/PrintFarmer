using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Extension Models
public class ExtensionListResponse
{
    [JsonPropertyName("agents")]
    public AgentInfo[] Agents { get; set; } = Array.Empty<AgentInfo>();
}
