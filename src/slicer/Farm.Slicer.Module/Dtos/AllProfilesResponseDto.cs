namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Combined response from worker /profiles endpoint containing all profile types.
/// Profiles are organized by manufacturer bundle to maintain hierarchy.
/// </summary>
public class AllProfilesResponseDto
{
    /// <summary>
    /// Gets or sets the profiles organized by manufacturer and model hierarchy.
    /// Structure: Manufacturer -> Model -> (Machine Profile + Associated Filament/Process Profiles).
    /// </summary>
    public Dictionary<string, ManufacturerProfilesDto> ByHierarchy { get; set; } = new();

    /// <summary>
    /// Gets or sets the machine model profiles (base templates from machine_model_list).
    /// These define base printer models like "Sovol SV08" that are NOT directly instantiatable.
    /// Grouped by manufacturer name.
    /// </summary>
    public Dictionary<string, IList<MachineModelProfileDto>> MachineModelProfiles { get; set; } = new();

    /// <summary>
    /// Gets or sets the legacy flat structure for backward compatibility.
    /// Machine profiles grouped by manufacturer name.
    /// </summary>
    public Dictionary<string, IList<MachineProfileDto>> MachineProfiles { get; set; } = new();

    /// <summary>
    /// Gets or sets the legacy flat structure for backward compatibility.
    /// Filament profiles grouped by manufacturer name.
    /// </summary>
    public Dictionary<string, IList<FilamentProfileDto>> FilamentProfiles { get; set; } = new();

    /// <summary>
    /// Gets or sets the legacy flat structure for backward compatibility.
    /// Process profiles grouped by manufacturer name.
    /// </summary>
    public Dictionary<string, IList<ProcessProfileDto>> ProcessProfiles { get; set; } = new();
}
