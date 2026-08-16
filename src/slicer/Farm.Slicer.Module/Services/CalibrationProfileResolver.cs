using System.Data.Common;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.Services;

/// <summary>Resolves the exact, explicitly selected profiles from the local slicer store.</summary>
public sealed class CalibrationProfileResolver(
    SlicerDbContext dbContext,
    ILogger<CalibrationProfileResolver> logger)
    : ICalibrationProfileResolver
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken);
        }
        catch (DbException exception)
        {
            logger.LogWarning(
                "Calibration profile persistence is unavailable ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                "Calibration profile persistence is not configured ({ExceptionType})",
                exception.GetType().Name);
            return false;
        }
    }

    public async Task<ResolvedCalibrationProfiles> ResolveAsync(
        Guid machineProfileId,
        Guid processProfileId,
        Guid filamentProfileId,
        CalibrationProfileAccessScope accessScope,
        CancellationToken cancellationToken)
    {
        MachineProfile? machine;
        ProcessProfile? process;
        FilamentProfile? filament;
        try
        {
            machine = await dbContext.MachineProfiles
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    profile =>
                        profile.Id == machineProfileId &&
                        (accessScope.BypassOwnership ||
                            profile.IsPublic ||
                            (accessScope.UserId.HasValue &&
                                profile.CreatedByUserId == accessScope.UserId.Value)),
                    cancellationToken);
            process = await dbContext.ProcessProfiles
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    profile =>
                        profile.Id == processProfileId &&
                        (accessScope.BypassOwnership ||
                            profile.IsPublic ||
                            (accessScope.UserId.HasValue &&
                                profile.CreatedByUserId == accessScope.UserId.Value)),
                    cancellationToken);
            filament = await dbContext.FilamentProfiles
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    profile =>
                        profile.Id == filamentProfileId &&
                        (accessScope.BypassOwnership ||
                            profile.IsPublic ||
                            (accessScope.UserId.HasValue &&
                                profile.CreatedByUserId == accessScope.UserId.Value)),
                    cancellationToken);
        }
        catch (DbException exception)
        {
            throw new CalibrationProfileResolverUnavailableException(
                "Calibration profile persistence could not be queried.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new CalibrationProfileResolverUnavailableException(
                "Calibration profile persistence is not available.",
                exception);
        }

        if (accessScope.BypassOwnership &&
            (machine?.IsPublic == false ||
                process?.IsPublic == false ||
                filament?.IsPublic == false))
        {
            logger.LogInformation(
                "Audited farm-admin profile bypass by user {UserId} for calibration profiles {MachineProfileId}, {ProcessProfileId}, and {FilamentProfileId}",
                accessScope.UserId,
                machineProfileId,
                processProfileId,
                filamentProfileId);
        }

        return new ResolvedCalibrationProfiles(
            MapMachine(machine),
            MapProcess(process),
            MapFilament(filament));
    }

    private static ResolvedCalibrationProfile? MapMachine(MachineProfile? profile)
    {
        if (profile is null)
        {
            return null;
        }

        MachineProfileDerivedFields derived = MachineProfileDerivedFieldsExtractor.Extract(profile.RawJson);
        return new(
            profile.Id,
            "machine",
            profile.Name,
            profile.SlicerType.ToString(),
            profile.SlicerDistribution,
            profile.SlicerVersion,
            profile.ProfileFormat,
            NormalizeUtc(profile.UpdatedAt),
            profile.RawJson,
            profile.Hash,
            profile.PrinterModelId,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            profile.Manufacturer,
            null,
            derived.PrintablePolygon,
            derived.BedOriginX,
            derived.BedOriginY,
            derived.BuildVolumeX,
            derived.BuildVolumeY,
            derived.BuildVolumeZ,
            derived.MotionType,
            derived.MaxAcceleration,
            derived.MaxTravelSpeed,
            derived.HasHeatedBed,
            derived.HasHeatedChamber,
            derived.NozzleDiameter,
            derived.NozzleType,
            derived.NozzleMaxTemperature,
            derived.HotendMaxTemperature);
    }

    private static ResolvedCalibrationProfile? MapProcess(ProcessProfile? profile) =>
        profile is null
            ? null
            : new(
                profile.Id,
                "process",
                profile.Name,
                profile.SlicerType.ToString(),
                profile.SlicerDistribution,
                profile.SlicerVersion,
                profile.ProfileFormat,
                NormalizeUtc(profile.UpdatedAt),
                profile.RawJson,
                profile.Hash,
                profile.PrinterModelId,
                profile.SpecificPrinterId,
                profile.CompatiblePrinters,
                profile.LayerHeight,
                profile.InfillPercentage,
                profile.PrintSpeed,
                null,
                null,
                null,
                null,
                null,
                null);

    private static ResolvedCalibrationProfile? MapFilament(FilamentProfile? profile) =>
        profile is null
            ? null
            : new(
                profile.Id,
                "filament",
                profile.Name,
                profile.SlicerType.ToString(),
                profile.SlicerDistribution,
                profile.SlicerVersion,
                profile.ProfileFormat,
                NormalizeUtc(profile.UpdatedAt),
                profile.RawJson,
                profile.Hash,
                null,
                null,
                profile.CompatiblePrinters,
                null,
                null,
                profile.PrintSpeed,
                profile.NozzleTemperature,
                profile.BedTemperature,
                null,
                profile.Material,
                profile.Manufacturer,
                null);

    private static DateTime? NormalizeUtc(DateTime value)
    {
        if (value == default)
        {
            return null;
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }
}
