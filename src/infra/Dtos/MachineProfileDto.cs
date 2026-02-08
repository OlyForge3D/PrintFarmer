using System.Text.Json.Serialization;

namespace Farm.Infrastructure;

/// <summary>
/// Machine/Printer profile DTO for OrcaSlicer.
/// Contains printer-specific configuration like bed size, extruders, etc.
/// </summary>
public class MachineProfileDto
{
    /// <summary>
    /// Gets or sets the profile name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the manufacturer name.
    /// </summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the profile description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the nozzle diameter (e.g., 0.4, 0.6, 0.8 mm).
    /// </summary>
    public double? NozzleDiameter { get; set; }

    /// <summary>
    /// Gets or sets the base printer model name from the profile (e.g., "Voron 2.4 350").
    /// This is the model name used for alias lookup to link profiles to catalog PrinterModels.
    /// </summary>
    [JsonPropertyName("printer_model")]
    public string? PrinterModel { get; set; }

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
