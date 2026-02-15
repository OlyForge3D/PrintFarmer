namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Composite slicer profile that combines machine, process, and filament profiles.
/// </summary>
public class SlicerProfileDto
{
    public MachineProfileDto? MachineProfile { get; set; }

    public ProcessProfileDto? ProcessProfile { get; set; }

    public FilamentProfileDto? FilamentProfile { get; set; }
}
