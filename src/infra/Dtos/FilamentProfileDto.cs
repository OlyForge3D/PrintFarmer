using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

/// <summary>
/// Filament/Material profile DTO for OrcaSlicer.
/// Contains material-specific settings like temperature, speed, etc.
/// </summary>
public class FilamentProfileDto
{
    /// <summary>
    /// Gets or sets the profile name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the material type (e.g., PLA, PETG, ABS).
    /// </summary>
    public string Material { get; set; } = "PLA";

    /// <summary>
    /// Gets or sets the manufacturer name.
    /// </summary>
    public string? Manufacturer { get; set; }

    /// <summary>
    /// Gets or sets the profile description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the nozzle temperature in °C.
    /// </summary>
    public int NozzleTemperature { get; set; } = 210;

    /// <summary>
    /// Gets or sets the bed temperature in °C.
    /// </summary>
    public int BedTemperature { get; set; } = 60;

    /// <summary>
    /// Gets or sets the print speed in mm/s.
    /// </summary>
    public int PrintSpeed { get; set; } = 50;

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
