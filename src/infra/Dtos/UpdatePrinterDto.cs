using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure;

/// <summary>
/// Update payload for modifying core printer attributes or reassigning catalog metadata.
/// </summary>
public record UpdatePrinterDto(
    string? Name = null,
    string? ServerUrl = null,
    string? Notes = null,
    Guid? ManufacturerId = null,
    Guid? ModelId = null,
    string? NewManufacturerName = null,
    string? NewModelName = null,
    DateTime? DateAcquired = null,
    PrinterBackend? Backend = null,
    string? ApiKey = null,

    /// <summary>
    /// Username for HTTP Digest authentication (primarily for PrusaLink).
    /// Defaults to "maker" for PrusaLink printers if not specified.
    /// </summary>
    string? Username = null,

    /// <summary>
    /// Password for HTTP Digest authentication (primarily for PrusaLink).
    /// User must obtain this from the printer's web interface under Settings → Network → Credentials.
    /// </summary>
    string? Password = null,

    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    string? OriginalServerUrl = null,

    // Printer capabilities
    double? NozzleDiameter = null,
    string[]? SupportedMaterials = null,
    double? MaxBuildVolumeX = null,
    double? MaxBuildVolumeY = null,
    double? MaxBuildVolumeZ = null,
    bool? HasHeatedBed = null,
    bool? HasEnclosure = null,
    bool? MultiMaterial = null,
    int? MaxHotendTemp = null,
    int? MaxBedTemp = null,
    bool? SupportsAutoLeveling = null,
    int? MaxPrintSpeed = null,
    int? BackendPort = null,
    int? FrontendPort = null,

    // Approval workflow
    bool? IsEnabled = null,

    // Toolheads - for updating individual toolhead settings
    UpdateToolheadDto[]? Toolheads = null);
