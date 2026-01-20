using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class SerialDevicesResponse
{
    [JsonPropertyName("serial_devices")]
    public SerialDevice[] SerialDevices { get; set; } = Array.Empty<SerialDevice>();
}
