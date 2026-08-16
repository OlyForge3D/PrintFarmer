using System.Text.RegularExpressions;

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

/// <summary>
/// Bounds/format validation for the manual toolhead metrology fields exposed by the
/// calibration-setup endpoint. These values feed calibration eligibility and,
/// downstream, slicer/G-code generation, so physically nonsensical input (e.g. a
/// nozzle offset of 1e300mm or a negative max volumetric flow) must be rejected with
/// 400 rather than silently persisted — a caller only needs the distinct
/// <c>calibration:update</c> permission (not <c>printers:admin</c>) to reach this
/// endpoint.
/// </summary>
public static class CalibrationSetupValidation
{
    /// <summary>Generous but finite bound on nozzle offsets, in millimeters.</summary>
    private const double MaxOffsetMagnitudeMm = 100;

    /// <summary>Generous upper bound on max volumetric flow, in mm^3/s.</summary>
    private const double MaxVolumetricFlowCeiling = 200;

    private static readonly Regex GearRatioPattern =
        new(@"^\d+(\.\d+)?\s*:\s*\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>
    /// Validates one toolhead's metrology fields, returning a problem payload
    /// (suitable for <c>BadRequest</c>) on the first violation, or <see langword="null"/>
    /// if every present field is within bounds.
    /// </summary>
    public static object? ValidateToolheadMetrology(CalibrationToolheadSetupDto toolhead)
    {
        foreach ((string field, double? value) in new (string, double?)[]
        {
            ("offsetX", toolhead.OffsetX),
            ("offsetY", toolhead.OffsetY),
            ("offsetZ", toolhead.OffsetZ),
        })
        {
            if (value is { } offset && (!double.IsFinite(offset) || Math.Abs(offset) > MaxOffsetMagnitudeMm))
            {
                return new
                {
                    error = "invalid_toolhead_metrology",
                    toolheadId = toolhead.Id,
                    field,
                    message = $"{field} must be a finite value within +/-{MaxOffsetMagnitudeMm}mm.",
                };
            }
        }

        if (toolhead.MaxVolumetricFlow is { } flow &&
            (!double.IsFinite(flow) || flow <= 0 || flow > MaxVolumetricFlowCeiling))
        {
            return new
            {
                error = "invalid_toolhead_metrology",
                toolheadId = toolhead.Id,
                field = "maxVolumetricFlow",
                message = $"maxVolumetricFlow must be greater than 0 and at most {MaxVolumetricFlowCeiling}mm^3/s.",
            };
        }

        if (!string.IsNullOrWhiteSpace(toolhead.ExtruderGearRatio) &&
            !GearRatioPattern.IsMatch(toolhead.ExtruderGearRatio))
        {
            return new
            {
                error = "invalid_toolhead_metrology",
                toolheadId = toolhead.Id,
                field = "extruderGearRatio",
                message = "extruderGearRatio must be formatted as 'numerator:denominator' (e.g. '3:1').",
            };
        }

        return null;
    }
}
