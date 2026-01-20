namespace Farm.Infrastructure.Contracts.Slicing.Libraries;

/// <summary>
/// Metadata about a single slicer profile.
/// </summary>
public record SlicerProfileMetadata(
    string Id,
    string Name,
    string Type,  // "printer", "filament", "process"
    string? Manufacturer = null,
    string? PrinterModel = null,
    string? Material = null,
    string? QualityLevel = null);
