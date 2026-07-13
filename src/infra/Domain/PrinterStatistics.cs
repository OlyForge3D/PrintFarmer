using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Tracks cumulative statistics for a printer across all print jobs (both PrintFarmer-managed and external).
/// Updated by the PrintStatisticsSyncService background service.
/// </summary>
public class PrinterStatistics
{
    /// <summary>
    /// Primary key - uses PrinterId as the key (one-to-one with Printer)
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Reference to the printer these statistics belong to
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Navigation property to the printer
    /// </summary>
    public Printer Printer { get; set; } = null!;

    /// <summary>
    /// Total cumulative print hours (from both PrintFarmer jobs and external prints)
    /// </summary>
    public double TotalPrintHours { get; set; }

    /// <summary>
    /// Cumulative print hours attributed ONLY to the external printer backend (Moonraker/OctoPrint
    /// history totals), snapshotted before PrintFarmer job aggregation inflates
    /// <see cref="TotalPrintHours"/>. Used as the cross-cycle baseline for per-toolhead wear
    /// attribution so PrintFarmer-job inflation cannot zero out the external delta (issue #711).
    /// </summary>
    public double ExternalPrintHours { get; set; }

    /// <summary>
    /// Number of completed jobs reported only by the external printer backend. This remains
    /// unchanged when external synchronization fails and is the clean baseline for adding
    /// absolute PrintFarmer job-history totals.
    /// </summary>
    public long ExternalJobsCompleted { get; set; }

    /// <summary>
    /// UTC timestamp captured the first time a trustworthy external baseline
    /// (<see cref="ExternalPrintHours"/> / <see cref="ExternalJobsCompleted"/>) was established for
    /// this printer, or <c>null</c> when the baseline has never been initialized (issue #711,
    /// round-7 Finding 1). A null sentinel means "not yet captured": the sync service must NOT
    /// snapshot a possibly PrintFarmer-inflated <see cref="TotalPrintHours"/> as the external
    /// baseline, must skip per-toolhead delta attribution, and (for a supported-but-failed
    /// external sync) must skip the reset-then-add aggregation so the inflated total is never
    /// permanently doubled. It is set on the first successful external sync (snapshotting the
    /// backend total) and on the first sync of a backend that cannot report external history
    /// (snapshotting an authoritative zero). Existing rows are seeded null by migration.
    /// </summary>
    public DateTime? ExternalBaselineInitializedUtc { get; set; }

    /// <summary>
    /// Total number of completed print jobs
    /// </summary>
    public int TotalJobsCompleted { get; set; }

    /// <summary>
    /// Total number of failed print jobs
    /// </summary>
    public int TotalJobsFailed { get; set; }

    /// <summary>
    /// Total filament used in grams
    /// </summary>
    public double TotalFilamentUsedGrams { get; set; }

    /// <summary>
    /// Total filament used in meters
    /// </summary>
    public double TotalFilamentUsedMeters { get; set; }

    /// <summary>
    /// Last time statistics were synced from printer API
    /// </summary>
    public DateTime LastSyncTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this record was first created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this record was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
