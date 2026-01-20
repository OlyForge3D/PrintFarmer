using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class PowerDeviceRequest
{
    [JsonPropertyName("device")]
    public string? Device { get; set; }

    [JsonPropertyName("devices")]
    public string[]? Devices { get; set; }
}
