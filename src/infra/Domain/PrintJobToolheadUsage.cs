using Farm.Infrastructure.Services.Spoolman;

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
    /// Owning Spoolman namespace captured when this row first receives
    /// authoritative actual usage. Null marks legacy or unqualified history.
    /// </summary>
    public SpoolSourceKind? SpoolSourceKind { get; private set; }

    /// <summary>
    /// Normalized identity of the owning Spoolman source. This value is write-once.
    /// </summary>
    public string? SpoolSourceIdentity { get; private set; }

    /// <summary>
    /// Actual filament consumed by this toolhead in grams.
    /// Null until the job completes and usage is calculated.
    /// </summary>
    public double? FilamentUsageGrams { get; set; }

    /// <summary>
    /// Whether <see cref="FilamentUsageGrams"/> came from a positive backend
    /// actual rather than a slicer estimate or other fallback.
    /// </summary>
    public bool IsFilamentUsageAuthoritative { get; private set; }

    /// <summary>
    /// Slicer's estimated filament usage for this toolhead in grams.
    /// Captured at job dispatch time from the gcode file's per-extruder metadata.
    /// Provides early visibility into expected consumption before job completion.
    /// </summary>
    public double? SlicerEstimateGrams { get; set; }

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

    /// <summary>
    /// Records the first positive backend actual and its immutable spool source.
    /// Later completion retries cannot rewrite or duplicate the sample.
    /// </summary>
    public bool RecordAuthoritativeUsage(
        double grams,
        CanonicalSpoolIdentity? identity)
    {
        if (IsFilamentUsageAuthoritative || grams <= 0 || !double.IsFinite(grams))
        {
            return false;
        }

        FilamentUsageGrams = grams;
        IsFilamentUsageAuthoritative = true;

        if (SpoolmanSpoolId.HasValue
            && identity.HasValue
            && identity.Value.SpoolId == SpoolmanSpoolId.Value
            && SpoolSourceKind is null
            && SpoolSourceIdentity is null)
        {
            SpoolSourceKind = identity.Value.SourceKind;
            SpoolSourceIdentity = identity.Value.SourceIdentity;
        }

        return true;
    }

    /// <summary>
    /// Records an estimate only while no authoritative actual has been captured.
    /// </summary>
    public void RecordEstimatedUsage(double grams)
    {
        if (!IsFilamentUsageAuthoritative && grams > 0 && double.IsFinite(grams))
        {
            FilamentUsageGrams = grams;
        }
    }
}
