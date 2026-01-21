using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

// Printer Capabilities DTOs
/// <summary>
/// Technical capabilities and current availability snapshot for a printer.
/// </summary>
public record PrinterCapabilitiesDto(
    Guid Id,
    Guid PrinterId,
    string PrinterName,
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    bool SupportsAutoLeveling = false,
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null,
    int? MaxPrintSpeed = null,
    [property: ImportExport(ImportExportTargets.Import)] string? CurrentMaterial = null,
    [property: ImportExport(ImportExportTargets.Import)] int? CurrentSpoolId = null,
    [property: ImportExport(ImportExportTargets.Import)] bool IsAvailable = true,
    DateTime LastUpdated = default);

/// <summary>
/// Creation payload for registering printer capabilities.
/// </summary>
public record CreatePrinterCapabilitiesDto(
    Guid PrinterId,
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null);

/// <summary>
/// Update payload for modifying an existing printer capabilities record.
/// </summary>
public record UpdatePrinterCapabilitiesDto(
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool HasHeatedBed = true,
    bool HasEnclosure = false,
    bool MultiMaterial = false,
    bool SupportsAutoLeveling = false,
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null,
    int? MaxPrintSpeed = null,
    [property: ImportExport(ImportExportTargets.Import)] string? CurrentMaterial = null,
    [property: ImportExport(ImportExportTargets.Import)] int? CurrentSpoolId = null,
    [property: ImportExport(ImportExportTargets.Import)] bool IsAvailable = true);

/// <summary>
/// Legacy / extended capabilities definition supporting multi-nozzle sets and feature flags.
/// </summary>
public class CreateOrUpdatePrinterCapabilitiesDto
{
    public decimal[]? NozzleDiameters { get; set; }

    public string[]? SupportedMaterials { get; set; }

    public decimal MaxPrintVolumeX { get; set; }

    public decimal MaxPrintVolumeY { get; set; }

    public decimal MaxPrintVolumeZ { get; set; }

    public int MaxHotendTemperature { get; set; }

    public int MaxBedTemperature { get; set; }

    public bool HasHeatedBed { get; set; }

    public bool HasEnclosure { get; set; }

    public bool SupportsAutoLeveling { get; set; }

    public int MaxPrintSpeed { get; set; }
}

/// <summary>
/// Lean capabilities export DTO (excludes redundant PrinterId/PrinterName already in parent PrinterWithCapabilitiesDto).
/// Used for export to keep JSON compact and avoid duplication.
/// Null properties are excluded from JSON export to keep payload minimal.
/// </summary>
public class PrinterCapabilitiesExportDto
{
    public Guid Id { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? NozzleDiameter { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? SupportedMaterials { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxBuildVolumeX { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxBuildVolumeY { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxBuildVolumeZ { get; set; }

    public bool HasHeatedBed { get; set; } = true;

    public bool HasEnclosure { get; set; } = false;

    public bool MultiMaterial { get; set; } = false;

    public bool SupportsAutoLeveling { get; set; } = false;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxHotendTemp { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxBedTemp { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentMaterial { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CurrentSpoolId { get; set; }

    public bool IsAvailable { get; set; } = true;

    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Combined printer identity with capabilities snapshot.
/// </summary>
public class PrinterWithCapabilitiesDto
{
    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public string PrinterModel { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrinterCapabilitiesExportDto? Capabilities { get; set; }

    // Additional export-friendly fields
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManufacturerName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PrinterBackend? Backend { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IpAddress { get; set; }

    // Import-friendly fields (for re-importing exported printers)
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerUrl { get; set; } // Base URL without port (e.g., "http://192.168.1.100")

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BackendPort { get; set; } // Backend API port (e.g., 7125 for Moonraker)

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FrontendPort { get; set; } // Frontend port if applicable (e.g., 5000 for PrusaLink)

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; set; }
}

/// <summary>
/// Scored compatibility result when matching a G-code file or job to candidate printers.
/// </summary>
public class CompatiblePrinterDto
{
    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public int CompatibilityScore { get; set; } // 0-100

    public string[] CompatibilityReasons { get; set; } = [];

    public int CurrentQueueLength { get; set; }
}
