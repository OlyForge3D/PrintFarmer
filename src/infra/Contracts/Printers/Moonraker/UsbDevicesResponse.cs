using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class UsbDevicesResponse
{
    [JsonPropertyName("usb_devices")]
    public UsbDevice[] UsbDevices { get; set; } = Array.Empty<UsbDevice>();
}
