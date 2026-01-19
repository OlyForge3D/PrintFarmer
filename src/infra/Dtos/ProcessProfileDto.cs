using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

/// <summary>
/// Process/Quality profile DTO for OrcaSlicer.
/// Contains quality/speed settings like layer height, infill, supports, etc.
/// </summary>
public class ProcessProfileDto
{
    /// <summary>
    /// Gets or sets the profile name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the quality preset (draft, standard, fine).
    /// </summary>
    public string Quality { get; set; } = "standard";

    /// <summary>
    /// Gets or sets the layer height in millimeters.
    /// </summary>
    public double LayerHeight { get; set; } = 0.2;

    /// <summary>
    /// Gets or sets the infill percentage (0-100).
    /// </summary>
    public int InfillPercentage { get; set; } = 20;

    /// <summary>
    /// Gets or sets the print speed in mm/s.
    /// </summary>
    public int PrintSpeed { get; set; } = 50;

    /// <summary>
    /// Gets or sets a value indicating whether support structures are enabled.
    /// </summary>
    public bool Supports { get; set; }

    /// <summary>
    /// Gets or sets the profile description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the list of compatible printer names.
    /// </summary>
    [JsonPropertyName("compatible_printers")]
    public IList<string> CompatiblePrinters { get; set; } = [];

    /// <summary>
    /// Gets or sets the raw compatible printers condition expression (internal use only).
    /// </summary>
    [JsonIgnore]
    public string? CompatiblePrintersCondition { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this profile can be instantiated (used) in the slicer.
    /// true = user-selectable, false = base/template profile for inheritance only.
    /// </summary>
    [JsonConverter(typeof(Json.StringToBoolJsonConverter))]
    public bool Instantiation { get; set; } = true;

    /// <summary>
    /// Gets or sets the parent profile name to inherit settings from (used during seeding for inheritance resolution).
    /// </summary>
    [JsonPropertyName("inherits")]
    public string? Inherits { get; set; }

    /// <summary>
    /// Gets or sets the additional settings dictionary.
    /// </summary>
    public Dictionary<string, object> Settings { get; set; } = new();
}
