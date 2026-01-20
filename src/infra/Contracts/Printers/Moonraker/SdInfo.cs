using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class SdInfo
{
    [JsonPropertyName("manufacturer_id")]
    public string ManufacturerId { get; set; } = string.Empty;

    [JsonPropertyName("manufacturer")]
    public string Manufacturer { get; set; } = string.Empty;

    [JsonPropertyName("oem_id")]
    public string OemId { get; set; } = string.Empty;

    [JsonPropertyName("product_name")]
    public string ProductName { get; set; } = string.Empty;

    [JsonPropertyName("product_revision")]
    public string ProductRevision { get; set; } = string.Empty;

    [JsonPropertyName("serial_number")]
    public string SerialNumber { get; set; } = string.Empty;

    [JsonPropertyName("manufacturer_date")]
    public string ManufacturerDate { get; set; } = string.Empty;

    [JsonPropertyName("capacity")]
    public string Capacity { get; set; } = string.Empty;

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }
}
