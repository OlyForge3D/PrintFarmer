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
