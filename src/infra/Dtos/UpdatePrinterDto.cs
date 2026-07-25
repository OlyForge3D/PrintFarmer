using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;

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
    string? Username = null,
    string? Password = null,

    string? CameraStreamUrl = null,
    string? CameraSnapshotUrl = null,
    string? OriginalServerUrl = null,
    string? BuddyCameraIp = null,

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

    // Cost tracking overrides
    decimal? Wattage = null,
    decimal? MachineHourlyRate = null,

    // Approval workflow
    bool? IsEnabled = null,

    // Obico AI failure detection opt-in
    bool? ObicoEnabled = null,

    // Z-offset calibration
    decimal? ZOffsetMm = null,

    // Bed surface type
    Guid? BedTypeId = null,

    // Auto-dispatch model defaults opt-out
    bool? UseModelDispatchDefaults = null,

    // Toolheads - for updating individual toolhead settings
    UpdateToolheadDto[]? Toolheads = null,

    // Explicit calibration compatibility identity
    PrinterFirmwareFamily? FirmwareFamily = null,
    PrinterGcodeDialect? GcodeDialect = null,
    FirmwareDetectionSource? FirmwareDetectionSource = null,
    string? FirmwareVersion = null,
    string? FirmwareDetectionVersion = null,
    [Range(typeof(decimal), "0", "1")]
    decimal? FirmwareDetectionConfidence = null,
    DateTime? FirmwareDetectedAtUtc = null,
    bool? FirmwareIdentityVerified = null,
    string? BackendVersion = null,
    string? BackendApiVersion = null,

    // Explicit calibration geometry, motion, and safety data
    double? BedOriginX = null,
    double? BedOriginY = null,
    CalibrationPointDto[]? PrintablePolygon = null,
    CalibrationExcludedRegionDto[]? ExcludedRegions = null,
    CalibrationMotionType? CalibrationMotionType = null,
    int? MaxTravelSpeed = null,
    int? MaxAcceleration = null,
    int? MaxTravelAcceleration = null,
    bool? CalibrationHasHeatedBed = null,
    bool? CalibrationHasEnclosure = null,
    bool? HasHeatedChamber = null,
    int? MaxChamberTemp = null,
    int? ActiveToolheadIndex = null,
    bool? SupportsPressureAdvance = null,
    bool? SupportsFirmwareRetraction = null,
    DateTime? CalibrationHardwareVerifiedAtUtc = null,

    // Explicit upstream OrcaSlicer profile selection
    string? CalibrationSlicerEngine = null,
    string? CalibrationSlicerDistribution = null,
    string? CalibrationSlicerVersion = null,
    string? CalibrationProfileFormat = null,
    Guid? CalibrationMachineProfileId = null,
    Guid? CalibrationProcessProfileId = null,
    Guid? CalibrationFilamentProfileId = null);
