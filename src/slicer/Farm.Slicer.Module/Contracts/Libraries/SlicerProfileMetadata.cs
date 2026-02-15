namespace Farm.Slicer.Module.Contracts.Libraries;

/// <summary>
/// Metadata about a single slicer profile.
/// </summary>
public record SlicerProfileMetadata(
    string Id,
    string Name,
    string Type,
    string? Manufacturer = null,
    string? PrinterModel = null,
    string? Material = null,
    string? QualityLevel = null);
