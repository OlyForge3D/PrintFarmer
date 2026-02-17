using System.Text.Json.Serialization;
using Farm.Slicer.Module.Json;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Machine/Printer profile DTO for OrcaSlicer.
/// </summary>
public class MachineProfileDto
{
    public string Name { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;

    public string? Description { get; set; }

    public double? NozzleDiameter { get; set; }

    [JsonPropertyName("printer_model")]
    public string? PrinterModel { get; set; }

    [JsonConverter(typeof(StringToBoolJsonConverter))]
    public bool Instantiation { get; set; } = true;

    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }

    public Dictionary<string, object> Settings { get; set; } = new();
}
