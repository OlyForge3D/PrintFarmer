using System.Text.Json.Serialization;
using Farm.Slicer.Module.Json;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Filament/Material profile DTO for OrcaSlicer.
/// </summary>
public class FilamentProfileDto
{
    public string Name { get; set; } = string.Empty;

    public string Material { get; set; } = "PLA";

    public string? Manufacturer { get; set; }

    public string? Description { get; set; }

    public int NozzleTemperature { get; set; } = 210;

    public int BedTemperature { get; set; } = 60;

    public int PrintSpeed { get; set; } = 50;

    [JsonPropertyName("compatible_printers")]
    public IList<string> CompatiblePrinters { get; set; } = [];

    [JsonIgnore]
    public string? CompatiblePrintersCondition { get; set; }

    [JsonConverter(typeof(StringToBoolJsonConverter))]
    public bool Instantiation { get; set; } = true;

    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }

    public Dictionary<string, object> Settings { get; set; } = new();
}
