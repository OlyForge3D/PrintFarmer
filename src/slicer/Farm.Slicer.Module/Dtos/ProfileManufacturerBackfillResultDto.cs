namespace Farm.Slicer.Module.Dtos;

/// <summary>
/// Result of repairing legacy custom manufacturer values on profile families.
/// </summary>
public sealed record ProfileManufacturerBackfillResultDto(
    int FamiliesUpdated,
    int VariantsUpdated,
    int Skipped);
