namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Repository for per-toolhead cumulative print-hour tracking (issue #711, F6).
///
/// <para>
/// Per-tool maintenance schedules must accrue wear against the hours their own toolhead has
/// printed, not the printer-wide <c>PrinterStatistics.TotalPrintHours</c> counter (which
/// advances whenever ANY toolhead prints). This repository exposes the per-toolhead
/// <c>Toolhead.CumulativePrintHours</c> counter for read (alert engine / projection) and the
/// increment path used by the statistics sync background service.
/// </para>
/// </summary>
public interface IToolheadStatisticsRepository
{
    /// <summary>
    /// Returns every toolhead of a printer mapped to its cumulative print-hours (including
    /// toolheads that have accrued zero hours so a per-tool schedule always resolves its
    /// baseline). Read-only / no-tracking.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, double>> GetCumulativeHoursByPrinterAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Returns a flat map of toolhead ID → cumulative print-hours for every toolhead belonging
    /// to any of the supplied printers. Read-only / no-tracking; used by fleet projections.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, double>> GetCumulativeHoursByPrintersAsync(IReadOnlyCollection<Guid> printerIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the cumulative print-hours for a single toolhead, or <c>null</c> when the
    /// toolhead does not exist. Read-only / no-tracking.
    /// </summary>
    Task<double?> GetCumulativeHoursAsync(Guid toolheadId, CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="deltaHours"/> to the active/primary physical toolhead of the
    /// printer. The active toolhead is the primary physical toolhead, or — when none is
    /// flagged primary — the lowest-index physical toolhead. Loads the toolhead tracked on the
    /// shared scoped <c>AppDbContext</c> and mutates it, but does NOT call
    /// <c>SaveChangesAsync</c>; the caller persists via its own unit-of-work save so the
    /// increment commits atomically with the printer-statistics upsert. Returns the toolhead
    /// that was credited, or <c>null</c> when the printer has no physical toolhead.
    /// </summary>
    Task<Guid?> IncrementActiveToolheadHoursAsync(Guid printerId, double deltaHours, CancellationToken ct = default);
}
