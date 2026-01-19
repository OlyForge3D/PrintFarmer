using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class MetadataScanRequest
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;
}
