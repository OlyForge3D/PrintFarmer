using System.Text.Json.Serialization;
using Farm.Slicer.Module.Json;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Process/Quality profile DTO for OrcaSlicer.
/// </summary>
public class ProcessProfileDto
{
    public string Name { get; set; } = string.Empty;

    public string Quality { get; set; } = "standard";

    public double LayerHeight { get; set; } = 0.2;

    public int InfillPercentage { get; set; } = 20;

    public int PrintSpeed { get; set; } = 50;

    // Normalized first-layer values: explicit Orca first-layer settings when present,
    // otherwise fallback to the profile's normal layer/speed values.
    public double FirstLayerHeight { get; set; } = 0.2;

    public int FirstLayerPrintSpeed { get; set; } = 50;

    public bool Supports { get; set; }

    public string? Description { get; set; }

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
