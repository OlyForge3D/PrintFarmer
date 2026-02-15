namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Response DTO for model-specific profile queries.
/// Contains all profiles (machine, filament, process) for a specific manufacturer and model.
/// </summary>
public class ModelProfilesResponseDto
{
    /// <summary>
    /// Manufacturer name (e.g., "Elegoo", "Prusa").
    /// </summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// Model name (e.g., "Centauri Carbon", "CORE One").
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Machine profiles for this model (one per nozzle size variant).
    /// </summary>
    public IList<MachineProfileDto> MachineProfiles { get; set; } = [];

    /// <summary>
    /// Process/print profiles compatible with this model.
    /// </summary>
    public IList<ProcessProfileDto> ProcessProfiles { get; set; } = [];

    /// <summary>
    /// Filament profiles compatible with this model.
    /// </summary>
    public IList<FilamentProfileDto> FilamentProfiles { get; set; } = [];
}
