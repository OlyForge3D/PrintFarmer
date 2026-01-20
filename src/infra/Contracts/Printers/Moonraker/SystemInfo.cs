using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Machine Request Models
public class SystemInfo
{
    [JsonPropertyName("system_info")]
    public SystemInfoData SystemInfoData { get; set; } = new();
}
