using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class VirtualizationInfo
{
    [JsonPropertyName("virt_type")]
    public string VirtType { get; set; } = string.Empty;

    [JsonPropertyName("virt_identifier")]
    public string VirtIdentifier { get; set; } = string.Empty;
}
