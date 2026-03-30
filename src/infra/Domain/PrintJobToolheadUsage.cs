namespace Farm.Infrastructure.Domain;

/// <summary>
/// Records per-toolhead filament usage for a single print job.
/// Each row captures which spool was loaded on a specific toolhead/MMU-gate
/// and how much filament that toolhead consumed during the job.
/// </summary>
public class PrintJobToolheadUsage
{
    /// <summary>
    /// Unique identifier for this usage record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The print job this usage record belongs to.
    /// </summary>
    public Guid PrintJobId { get; set; }

    /// <summary>
    /// Zero-based toolhead index matching gcode T-commands (T0 = 0, T1 = 1, etc.).
    /// </summary>
    public int ToolheadIndex { get; set; }

    /// <summary>
    /// Spoolman spool ID that was loaded on this toolhead when the job ran.
    /// Null if no spool was tracked.
    /// </summary>
    public int? SpoolmanSpoolId { get; set; }

    /// <summary>
    /// Actual filament consumed by this toolhead in grams.
    /// Null until the job completes and usage is calculated.
    /// </summary>
    public double? FilamentUsageGrams { get; set; }

    /// <summary>
    /// Display name of the filament loaded on this toolhead (e.g., "PLA Basic").
    /// Denormalized from Spoolman for display without an external API call.
    /// </summary>
    public string? FilamentName { get; set; }

    /// <summary>
    /// Hex color of the filament loaded on this toolhead (e.g., "#FF0000").
    /// Denormalized from Spoolman for display.
    /// </summary>
    public string? FilamentColor { get; set; }

    /// <summary>
    /// Material cost in USD attributed to this toolhead's filament usage.
    /// Populated by the cost calculation service on job completion (Phase 5).
    /// </summary>
    public decimal? MaterialCostUsd { get; set; }

    /// <summary>
    /// Navigation property to the parent print job.
    /// </summary>
    public PrintJob PrintJob { get; set; } = null!;
}
