namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Composite slicer profile that combines machine, process, and filament profiles.
/// </summary>
public class SlicerProfileDto
{
    public MachineProfileDto? MachineProfile { get; set; }

    public ProcessProfileDto? ProcessProfile { get; set; }

    public FilamentProfileDto? FilamentProfile { get; set; }

    /// <summary>
    /// Per-extruder filament profiles for multi-toolhead printers.
    /// Index corresponds to extruder index. When populated, takes precedence over
    /// <see cref="FilamentProfile"/> for CLI invocation.
    /// </summary>
    public List<FilamentProfileDto>? ExtruderFilamentProfiles { get; set; }
}
