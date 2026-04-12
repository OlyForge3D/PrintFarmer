using System.Text.Json.Serialization;
using Farm.Slicer.Module.Json;

namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Machine model profile DTO for OrcaSlicer's machine_model_list.
/// </summary>
public class MachineModelProfileDto
{
    public string Name { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;

    public string? Description { get; set; }

    [JsonConverter(typeof(StringToBoolJsonConverter))]
    public bool Instantiation { get; set; }

    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }

    public Dictionary<string, string> Settings { get; set; } = new();
}
