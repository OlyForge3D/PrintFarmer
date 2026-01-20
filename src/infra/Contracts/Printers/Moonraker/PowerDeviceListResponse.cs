using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Power Device Models
public class PowerDeviceListResponse
{
    [JsonPropertyName("devices")]
    public PowerDevice[] Devices { get; set; } = Array.Empty<PowerDevice>();
}
