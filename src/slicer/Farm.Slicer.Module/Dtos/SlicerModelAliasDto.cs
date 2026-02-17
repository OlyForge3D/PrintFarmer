namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Slicer-specific model name alias mapping (e.g., OrcaSlicer "Prusa MK4" -> PrusaSlicer "MK4").
/// </summary>
public record SlicerModelAliasDto(
    Guid Id,
    Guid PrinterModelId,
    string SlicerModelName,
    string? SlicerType); // "OrcaSlicer", "PrusaSlicer", or null if applies to all slicers
