namespace Farm.Infrastructure.PrinterCalibration;

/// <summary>
/// Per-toolhead manual metrology fields that are never derivable from a resolved
/// OrcaSlicer machine profile (issue #1616, PR-3 of the #1613 calibration-eligibility
/// decomposition). These apply to every toolhead, including the active one — only the
/// active toolhead's nozzle geometry (diameter/type/etc.) is profile-derived; offsets,
/// drive type, and gear ratio are always manual.
/// </summary>
public sealed record CalibrationToolheadSetupDto(
    Guid Id,
    double? OffsetX = null,
    double? OffsetY = null,
    double? OffsetZ = null,
    string? NozzleMaterial = null,
    bool? NozzleIsHardened = null,
    double? MaxVolumetricFlow = null,
    string? DriveType = null,
    bool? IsDirectDrive = null,
    string? ExtruderGearRatio = null);

/// <summary>
/// Request payload for the dedicated calibration-setup endpoint. Deliberately narrower
/// than the raw printer-update DTO: it exposes only the residual fields that remain
/// genuinely manual after profile-owned sourcing (#1614/#1615), and has no
/// firmware family/version/gcodeDialect property at all — firmware identity is
/// read-only/display-only here; <c>FirmwareIdentityVerified</c> is the only
/// firmware-related field, and it is a confirm-only sign-off, not an override.
/// </summary>
public sealed record CalibrationSetupRequestDto(
    int? ActiveToolheadIndex = null,
    CalibrationExcludedRegionDto[]? ExcludedRegions = null,
    bool? SupportsPressureAdvance = null,
    bool? SupportsFirmwareRetraction = null,
    DateTime? CalibrationHardwareVerifiedAtUtc = null,
    bool? FirmwareIdentityVerified = null,
    CalibrationToolheadSetupDto[]? Toolheads = null);

/// <summary>Persisted state of a single toolhead's manual metrology, echoed back after a setup write.</summary>
public sealed record CalibrationToolheadSetupResultDto(
    Guid Id,
    int Index,
    string? Name,
    double? OffsetX,
    double? OffsetY,
    double? OffsetZ,
    string? NozzleMaterial,
    bool? NozzleIsHardened,
    double? MaxVolumetricFlow,
    string? DriveType,
    bool? IsDirectDrive,
    string? ExtruderGearRatio);

/// <summary>
/// Response returned by the calibration-setup endpoint: the persisted state of every
/// residual field plus enough concurrency/identity context (revision, ETag) for the
/// caller to issue a follow-up write, and a read-only firmware identity snapshot for
/// display alongside the confirm-only verified flag.
/// </summary>
public sealed record CalibrationSetupResultDto(
    Guid PrinterId,
    long ConfigurationRevision,
    string? RowVersion,
    int? ActiveToolheadIndex,
    IReadOnlyList<CalibrationExcludedRegionDto> ExcludedRegions,
    bool? SupportsPressureAdvance,
    bool? SupportsFirmwareRetraction,
    DateTime? CalibrationHardwareVerifiedAtUtc,
    CalibrationFirmwareIdentityDto Firmware,
    IReadOnlyList<CalibrationToolheadSetupResultDto> Toolheads);
