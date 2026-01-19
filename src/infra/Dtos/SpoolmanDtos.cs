namespace Farm.Infrastructure;

/// <summary>
/// Result of importing filament types from Spoolman.
/// </summary>
public record SpoolmanFilamentImportResult(
    int ImportedCount,
    int SkippedCount,
    int TotalSpoolmanMaterials,
    string[] ImportedNames);

/// <summary>
/// Represents a material type definition from Spoolman's /api/v1/material endpoint
/// </summary>
public record SpoolmanMaterialDto(
    int Id,
    string Name,
    double? Density = null,
    string? ColorHex = null);

/// <summary>
/// Result of probing a Spoolman endpoint (used by the setup flow and health probes).
/// </summary>
public record SpoolmanProbeResult(
    bool Success,
    string? NormalizedUrl = null,
    string? EndpointTried = null,
    int? StatusCode = null,
    string? Version = null,
    string? Message = null,
    string? ErrorCategory = null);
