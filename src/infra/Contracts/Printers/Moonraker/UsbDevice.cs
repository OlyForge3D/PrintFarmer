using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class UsbDevice
{
    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    [JsonPropertyName("vendor_id")]
    public string VendorId { get; set; } = string.Empty;

    [JsonPropertyName("product_id")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("manufacturer")]
    public string Manufacturer { get; set; } = string.Empty;

    [JsonPropertyName("product")]
    public string Product { get; set; } = string.Empty;
}
