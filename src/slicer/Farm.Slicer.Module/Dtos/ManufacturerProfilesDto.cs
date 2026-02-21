namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Represents all profile models for a single manufacturer.
/// Maps model identifiers to their machine profile and associated filament/process profiles.
/// </summary>
public class ManufacturerProfilesDto
{
    /// <summary>
    /// Gets or sets the manufacturer name (e.g., "Prusa", "Creality").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the models for this manufacturer, keyed by model_id (e.g., "Prusa_CORE_One", "Prusa_MK4S").
    /// Value contains the machine profile and associated filament/process profiles.
    /// </summary>
    public Dictionary<string, PrinterModelProfilesDto> Models { get; set; } = new();
}
