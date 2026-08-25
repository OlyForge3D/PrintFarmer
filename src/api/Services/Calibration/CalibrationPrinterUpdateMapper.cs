using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.Calibration;

internal static class CalibrationPrinterUpdateMapper
{
    /// <summary>Generous but finite bound on nozzle offsets, in millimeters.</summary>
    private const double MaxOffsetMagnitudeMm = 100;

    /// <summary>Generous upper bound on max volumetric flow, in mm^3/s.</summary>
    private const double MaxVolumetricFlowCeiling = 200;

    private static readonly Regex GearRatioPattern =
        new(@"^\d+(\.\d+)?\s*:\s*\d+(\.\d+)?$", RegexOptions.Compiled);

    /// <summary>
    /// Bounds/format validation for the manual toolhead metrology fields
    /// (<see cref="UpdateToolheadDto.OffsetX"/>/<see cref="UpdateToolheadDto.OffsetY"/>/
    /// <see cref="UpdateToolheadDto.OffsetZ"/>, <see cref="UpdateToolheadDto.MaxVolumetricFlow"/>,
    /// <see cref="UpdateToolheadDto.ExtruderGearRatio"/>). These values feed calibration
    /// context resolution and, downstream, slicer/G-code generation, so physically nonsensical
    /// input (e.g. a nozzle offset of 1e300mm or a negative max volumetric flow) must be
    /// rejected with 400 rather than silently persisted. Previously enforced only by the
    /// now-removed calibration-setup endpoint (#1942); applied here so the general
    /// <c>PUT /api/printers/{id}</c> endpoint enforces the same bounds.
    /// </summary>
    /// <returns>A problem payload (suitable for <c>BadRequest</c>) on the first violation, or
    /// <see langword="null"/> if every present field is within bounds.</returns>
    public static object? ValidateToolheadMetrology(UpdateToolheadDto toolhead)
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

    public static bool ApplyPrinter(Printer printer, UpdatePrinterDto update)
    {
        bool changed = false;

        changed |= Set(update.FirmwareFamily, printer.FirmwareFamily, value => printer.FirmwareFamily = value);
        changed |= Set(update.GcodeDialect, printer.GcodeDialect, value => printer.GcodeDialect = value);
        changed |= Set(update.FirmwareDetectionSource, printer.FirmwareDetectionSource, value => printer.FirmwareDetectionSource = value);
        changed |= SetString(update.FirmwareVersion, printer.FirmwareVersion, value => printer.FirmwareVersion = value);
        changed |= SetString(update.FirmwareDetectionVersion, printer.FirmwareDetectionVersion, value => printer.FirmwareDetectionVersion = value);
        changed |= Set(update.FirmwareDetectionConfidence, printer.FirmwareDetectionConfidence, value => printer.FirmwareDetectionConfidence = value);
        changed |= SetDate(update.FirmwareDetectedAtUtc, printer.FirmwareDetectedAtUtc, value => printer.FirmwareDetectedAtUtc = value);
        changed |= Set(update.FirmwareIdentityVerified, printer.FirmwareIdentityVerified, value => printer.FirmwareIdentityVerified = value);
        changed |= SetString(update.BackendVersion, printer.BackendVersion, value => printer.BackendVersion = value);
        changed |= SetString(update.BackendApiVersion, printer.BackendApiVersion, value => printer.BackendApiVersion = value);

        changed |= Set(update.BedOriginX, printer.BedOriginX, value => printer.BedOriginX = value);
        changed |= Set(update.BedOriginY, printer.BedOriginY, value => printer.BedOriginY = value);
        changed |= SetJson(update.PrintablePolygon, printer.PrintablePolygonJson, value => printer.PrintablePolygonJson = value);
        changed |= SetJson(update.ExcludedRegions, printer.ExcludedRegionsJson, value => printer.ExcludedRegionsJson = value);
        changed |= Set(update.CalibrationMotionType, printer.CalibrationMotionType, value => printer.CalibrationMotionType = value);
        changed |= Set(update.MaxTravelSpeed, printer.MaxTravelSpeed, value => printer.MaxTravelSpeed = value);
        changed |= Set(update.MaxAcceleration, printer.MaxAcceleration, value => printer.MaxAcceleration = value);
        changed |= Set(update.MaxTravelAcceleration, printer.MaxTravelAcceleration, value => printer.MaxTravelAcceleration = value);
        changed |= Set(update.CalibrationHasHeatedBed, printer.CalibrationHasHeatedBed, value => printer.CalibrationHasHeatedBed = value);
        changed |= Set(update.CalibrationHasEnclosure, printer.CalibrationHasEnclosure, value => printer.CalibrationHasEnclosure = value);
        changed |= Set(update.HasHeatedChamber, printer.HasHeatedChamber, value => printer.HasHeatedChamber = value);
        changed |= Set(update.MaxChamberTemp, printer.MaxChamberTemp, value => printer.MaxChamberTemp = value);
        changed |= Set(update.ActiveToolheadIndex, printer.ActiveToolheadIndex, value => printer.ActiveToolheadIndex = value);
        changed |= Set(update.SupportsPressureAdvance, printer.SupportsPressureAdvance, value => printer.SupportsPressureAdvance = value);
        changed |= Set(update.SupportsFirmwareRetraction, printer.SupportsFirmwareRetraction, value => printer.SupportsFirmwareRetraction = value);
        changed |= SetDate(
            update.CalibrationHardwareVerifiedAtUtc,
            printer.CalibrationHardwareVerifiedAtUtc,
            value => printer.CalibrationHardwareVerifiedAtUtc = value);

        changed |= SetString(update.CalibrationSlicerEngine, printer.CalibrationSlicerEngine, value => printer.CalibrationSlicerEngine = value);
        changed |= SetString(update.CalibrationSlicerDistribution, printer.CalibrationSlicerDistribution, value => printer.CalibrationSlicerDistribution = value);
        changed |= SetString(update.CalibrationSlicerVersion, printer.CalibrationSlicerVersion, value => printer.CalibrationSlicerVersion = value);
        changed |= SetString(update.CalibrationProfileFormat, printer.CalibrationProfileFormat, value => printer.CalibrationProfileFormat = value);
        changed |= SetProfileId(update.CalibrationMachineProfileId, printer.CalibrationMachineProfileId, value => printer.CalibrationMachineProfileId = value);
        changed |= SetProfileId(update.CalibrationProcessProfileId, printer.CalibrationProcessProfileId, value => printer.CalibrationProcessProfileId = value);
        changed |= SetProfileId(update.CalibrationFilamentProfileId, printer.CalibrationFilamentProfileId, value => printer.CalibrationFilamentProfileId = value);

        return changed;
    }

    public static bool ApplyToolhead(Toolhead toolhead, UpdateToolheadDto update)
    {
        bool changed = false;
        changed |= Set(update.ToolheadType, toolhead.ToolheadType, value => toolhead.ToolheadType = value);
        changed |= Set(update.OffsetX, toolhead.OffsetX, value => toolhead.OffsetX = value);
        changed |= Set(update.OffsetY, toolhead.OffsetY, value => toolhead.OffsetY = value);
        changed |= Set(update.OffsetZ, toolhead.OffsetZ, value => toolhead.OffsetZ = value);
        changed |= Set(update.NozzleDiameter, toolhead.NozzleDiameter, value => toolhead.NozzleDiameter = value);
        changed |= Set(update.NozzleType, toolhead.NozzleType, value => toolhead.NozzleType = value);
        changed |= SetString(update.NozzleMaterial, toolhead.NozzleMaterial, value => toolhead.NozzleMaterial = value);
        changed |= Set(update.NozzleMaxTemperature, toolhead.NozzleMaxTemperature, value => toolhead.NozzleMaxTemperature = value);
        changed |= Set(update.NozzleIsHardened, toolhead.NozzleIsHardened, value => toolhead.NozzleIsHardened = value);
        changed |= Set(update.HotendMaxTemperature, toolhead.HotendMaxTemperature, value => toolhead.HotendMaxTemperature = value);
        changed |= Set(update.MaxVolumetricFlow, toolhead.MaxVolumetricFlow, value => toolhead.MaxVolumetricFlow = value);
        changed |= SetString(update.DriveType, toolhead.DriveType, value => toolhead.DriveType = value);
        changed |= Set(update.IsDirectDrive, toolhead.IsDirectDrive, value => toolhead.IsDirectDrive = value);
        changed |= SetString(update.ExtruderGearRatio, toolhead.ExtruderGearRatio, value => toolhead.ExtruderGearRatio = value);
        return changed;
    }

    private static bool Set<T>(T? requested, T current, Action<T> assign)
        where T : struct
    {
        if (!requested.HasValue || EqualityComparer<T>.Default.Equals(requested.Value, current))
        {
            return false;
        }

        assign(requested.Value);
        return true;
    }

    private static bool Set<T>(T? requested, T? current, Action<T?> assign)
        where T : struct
    {
        if (!requested.HasValue || EqualityComparer<T?>.Default.Equals(requested, current))
        {
            return false;
        }

        assign(requested);
        return true;
    }

    private static bool SetString(
        string? requested,
        string? current,
        Action<string?> assign)
    {
        if (requested is null)
        {
            return false;
        }

        string? normalized = string.IsNullOrWhiteSpace(requested)
            ? null
            : requested.Trim();
        if (string.Equals(normalized, current, StringComparison.Ordinal))
        {
            return false;
        }

        assign(normalized);
        return true;
    }

    private static bool SetDate(
        DateTime? requested,
        DateTime? current,
        Action<DateTime?> assign)
    {
        if (!requested.HasValue)
        {
            return false;
        }

        DateTime normalized = requested.Value.Kind switch
        {
            DateTimeKind.Utc => requested.Value,
            DateTimeKind.Local => requested.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(requested.Value, DateTimeKind.Utc),
        };
        if (current == normalized)
        {
            return false;
        }

        assign(normalized);
        return true;
    }

    private static bool SetJson<T>(
        T[]? requested,
        string? current,
        Action<string?> assign)
    {
        if (requested is null)
        {
            return false;
        }

        string json = JsonSerializer.Serialize(requested);
        if (string.Equals(json, current, StringComparison.Ordinal))
        {
            return false;
        }

        assign(json);
        return true;
    }

    private static bool SetProfileId(
        Guid? requested,
        Guid? current,
        Action<Guid?> assign)
    {
        if (!requested.HasValue)
        {
            return false;
        }

        Guid? normalized = requested.Value == Guid.Empty ? null : requested;
        if (normalized == current)
        {
            return false;
        }

        assign(normalized);
        return true;
    }
}
