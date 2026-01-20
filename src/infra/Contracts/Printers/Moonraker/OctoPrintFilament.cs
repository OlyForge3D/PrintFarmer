using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class OctoPrintFilament
{
    [JsonPropertyName("length")]
    public double? Length { get; set; }
}
