namespace Farm.Infrastructure;

/// <summary>
/// Per-toolhead filament usage data for a print job.
/// One record per toolhead/MMU-gate that was active during the job.
/// </summary>
public record PrintJobToolheadUsageDto(
    Guid Id,
    Guid PrintJobId,
    int ToolheadIndex,
    int? SpoolmanSpoolId = null,
    double? FilamentUsageGrams = null,
    string? FilamentName = null,
    string? FilamentColor = null,
    decimal? MaterialCostUsd = null);
