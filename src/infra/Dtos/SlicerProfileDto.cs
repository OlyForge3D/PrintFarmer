namespace Farm.Infrastructure;

/// <summary>
/// Composite slicer profile that combines machine, process (quality), and filament profiles.
/// This is the primary profile object passed when slicing a model - it contains all three
/// profile types needed for a complete slicing operation.
/// </summary>
public class SlicerProfileDto
{
    /// <summary>
    /// Machine/printer profile controlling hardware-specific settings (bed size, extruders, etc.)
    /// </summary>
    public MachineProfileDto? MachineProfile { get; set; }

    /// <summary>
    /// Process/quality profile controlling print characteristics (layer height, infill, speed, supports).
    /// </summary>
    public ProcessProfileDto? ProcessProfile { get; set; }

    /// <summary>
    /// Filament/material profile controlling material-specific settings (temperatures, speeds, material type).
    /// </summary>
    public FilamentProfileDto? FilamentProfile { get; set; }
}
