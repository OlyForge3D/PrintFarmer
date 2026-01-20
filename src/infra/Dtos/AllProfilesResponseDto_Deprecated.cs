namespace Farm.Infrastructure;

/// <summary>
/// Legacy: Machine profiles grouped by manufacturer name (from bundle file name).
/// Example: { "Prusa": [machine1, machine2, ...], "Creality": [...], ... }.
/// </summary>
#pragma warning disable S101 // Naming convention required for backward compatibility
public class AllProfilesResponseDto_Deprecated
#pragma warning restore S101
{
    /// <summary>
    /// Gets or sets the machine profiles grouped by manufacturer name (from bundle file name).
    /// Example: { "Prusa": [machine1, machine2, ...], "Creality": [...], ... }.
    /// </summary>
    public Dictionary<string, IList<MachineProfileDto>> MachineProfiles { get; set; } = new();

    /// <summary>
    /// Gets or sets the filament profiles grouped by manufacturer name.
    /// </summary>
    public Dictionary<string, IList<FilamentProfileDto>> FilamentProfiles { get; set; } = new();

    /// <summary>
    /// Gets or sets the process profiles grouped by manufacturer name.
    /// </summary>
    public Dictionary<string, IList<ProcessProfileDto>> ProcessProfiles { get; set; } = new();
}
