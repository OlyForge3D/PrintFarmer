using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// Print Management Models
public class PrintStartRequest
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;
}
