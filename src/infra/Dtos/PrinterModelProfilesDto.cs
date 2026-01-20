namespace Farm.Infrastructure;

/// <summary>
/// Represents all profiles for a single printer model.
/// A model has one machine profile and multiple filament/process profiles.
/// </summary>
public class PrinterModelProfilesDto
{
    /// <summary>
    /// Gets or sets the human-readable model name (e.g., "Prusa CORE One", "Creality Ender 3 V2").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model identifier from the machine profile (e.g., "Prusa_CORE_One").
    /// </summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the machine profiles for this model (multiple per model: one per nozzle size variant).
    /// </summary>
    public IList<MachineProfileDto> MachineProfiles { get; set; } = [];

    /// <summary>
    /// Gets or sets the filament profiles applicable to this model.
    /// Multiple profiles per model (e.g., PLA, PETG, ABS variants).
    /// </summary>
    public IList<FilamentProfileDto> FilamentProfiles { get; set; } = [];

    /// <summary>
    /// Gets or sets the process/print profiles applicable to this model.
    /// Multiple profiles per model (e.g., draft, normal, quality variants).
    /// </summary>
    public IList<ProcessProfileDto> ProcessProfiles { get; set; } = [];
}
