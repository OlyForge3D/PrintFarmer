using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Spoolman;

/// <summary>
/// Computes spool coverage and predicted runout for one printer or the whole
/// fleet (see issue #709). All results consume the same ingredients:
/// <list type="bullet">
///   <item>Live per-toolhead spool bindings and denormalized display fields.</item>
///   <item>G-code per-extruder filament usage metadata.</item>
///   <item>The currently active print job and any print jobs Assigned/Queued
///   explicitly against the target printer. Unassigned shared-queue jobs are
///   candidate demand and must NOT be charged to a printer here.</item>
///   <item>Live progress from <see cref="Printers.IPrintersService"/> to prorate
///   the active job's remaining demand.</item>
/// </list>
///
/// Coverage is inherently a read-only projection. Actual-consumption
/// reconciliation on job completion is owned by
/// <see cref="Printers.PrintJobCompletionService"/>; this service never mutates
/// spool remaining weights.
/// </summary>
public interface IFilamentCoverageService
{
    /// <summary>
    /// Computes coverage for a single printer.
    /// </summary>
    Task<PrinterFilamentCoverageDto?> GetForPrinterAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Computes one printer's coverage with nullable origin provenance.
    /// </summary>
    async Task<FilamentCoverageResult<PrinterFilamentCoverageDto?>> GetForPrinterWithOriginAsync(
        Guid printerId,
        CancellationToken ct)
    {
        PrinterFilamentCoverageDto? coverage =
            await GetForPrinterAsync(printerId, ct).ConfigureAwait(false);
        return new FilamentCoverageResult<PrinterFilamentCoverageDto?>(
            coverage,
            OriginWatermark: null);
    }

    /// <summary>
    /// Computes coverage for every printer in the fleet. Concurrency and
    /// per-printer timeouts are bounded by
    /// <see cref="Settings.SpoolCoverageSettings"/>.
    /// </summary>
    Task<FleetFilamentCoverageDto> GetForFleetAsync(CancellationToken ct);

    /// <summary>
    /// Computes fleet coverage with nullable origin provenance.
    /// </summary>
    async Task<FilamentCoverageResult<FleetFilamentCoverageDto>> GetForFleetWithOriginAsync(CancellationToken ct)
    {
        FleetFilamentCoverageDto coverage = await GetForFleetAsync(ct).ConfigureAwait(false);
        return new FilamentCoverageResult<FleetFilamentCoverageDto>(
            coverage,
            OriginWatermark: null);
    }
}

/// <summary>
/// A filament-coverage projection with its conservative origin watermark.
/// </summary>
public sealed record FilamentCoverageResult<T>(T Value, long? OriginWatermark);
