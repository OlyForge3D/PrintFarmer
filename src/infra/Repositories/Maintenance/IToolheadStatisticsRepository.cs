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
    /// Returns the printer's physical toolheads mapped from their backend/G-code tool index to ID.
    /// MMU/AMS gates are excluded because they are filament sources, not wear-bearing hotends.
    /// </summary>
    Task<IReadOnlyDictionary<int, Guid>> GetPhysicalToolheadIdsByIndexAsync(
        Guid printerId,
        CancellationToken ct = default);

    /// <summary>
    /// Credits explicit per-toolhead print-hours described by <paramref name="attribution"/> to the
    /// printer's physical toolheads (issue #711, round-7 Finding 3). Only positive weights whose
    /// toolhead belongs to the printer and is <c>Physical</c> are applied; MMU/AMS gate toolheads are
    /// never wear sources. This mutates tracked entities without calling <c>SaveChangesAsync</c> so
    /// the increment commits atomically with the caller's unit-of-work. Returns the credited
    /// toolhead IDs, or an empty list when nothing positive applies.
    /// </summary>
    Task<IReadOnlyList<Guid>> ApplyToolheadHoursAsync(Guid printerId, ToolheadHourAttribution attribution, CancellationToken ct = default);
}
