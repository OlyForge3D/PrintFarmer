using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

/// <summary>
/// Machine model profile DTO for OrcaSlicer's machine_model_list.
/// These are base/template profiles that define the printer model (e.g., "Sovol SV08")
/// and are NOT directly instantiatable - they serve as parents for actual machine profiles.
/// </summary>
public class MachineModelProfileDto
{
    /// <summary>
    /// Gets or sets the profile name (e.g., "Sovol SV08", "Prusa MK4").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the manufacturer name (e.g., "Sovol", "Prusa").
    /// </summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the profile description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets whether this profile can be instantiated (should always be false for model profiles).
    /// </summary>
    [JsonConverter(typeof(Json.StringToBoolJsonConverter))]
    public bool Instantiation { get; set; } = false;

    /// <summary>
    /// Gets or sets the parent profile name to inherit settings from.
    /// </summary>
    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }

    /// <summary>
    /// Gets or sets the additional settings dictionary.
    /// Contains bed_shape, build_volume, etc.
    /// </summary>
    public Dictionary<string, object> Settings { get; set; } = new();
}
