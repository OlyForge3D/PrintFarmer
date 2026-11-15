using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Background-service-managed state for a printer.
/// Isolated from the main Printer row to prevent xmin concurrency conflicts
/// when background services write timestamps while users edit printer config.
/// </summary>
public class PrinterServiceState : IRevisionedEntity
{
    public Guid PrinterId { get; set; }

    public Printer Printer { get; set; } = null!;

    /// <summary>Most recent history job timestamp imported. Used for incremental seeding.</summary>
    public DateTime? LastHistorySeedUtc { get; set; }

    /// <summary>When the catalog model template was last applied.</summary>
    public DateTime? LastModelSyncAt { get; set; }

    /// <summary>When hardware capabilities were last updated.</summary>
    public DateTime LastCapabilityUpdate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the maintenance alert engine last evaluated this printer. Used as the staleness key
    /// for <c>MaintenanceAlertHostedService</c>'s keyset rotation so every printer is evaluated
    /// within a bounded number of intervals instead of only the first N (issue #2061). Null means
    /// never evaluated, which sorts first (most stale).
    /// </summary>
    public DateTime? LastMaintenanceAlertEvaluatedAt { get; set; }

    /// <summary>
    /// When <c>PrintStatsSyncHostedService</c> last ATTEMPTED to sync this printer's statistics,
    /// regardless of whether that attempt succeeded. Used as the staleness key for the stats-sync
    /// rotation (issue #2061) instead of <see cref="PrinterStatistics.LastSyncTime"/>, which only
    /// advances on an actual successful backend read and keeps its own meaning for backend-history
    /// math elsewhere. Decoupling the two means a printer whose sync keeps failing (backend
    /// offline, auth error, ...) still rotates out of the front of the queue every tick instead of
    /// permanently starving every other printer behind it. Null means never attempted, which sorts
    /// first (most stale).
    /// </summary>
    public DateTime? LastStatsSyncAttemptedAt { get; set; }

    /// <summary>Obico pool slot held by the current print, not a printer preference.</summary>
    public Guid? ObicoServerId { get; set; }

    public ObicoServer? ObicoServer { get; set; }

    /// <summary>Identity of the print holding this route; legacy pins retain a null key until activity is confirmed.</summary>
    [MaxLength(100)]
    public string? ObicoPrintKey { get; set; }

    /// <summary>Legacy global-only destination selected for the current print.</summary>
    [MaxLength(500)]
    public string? ObicoFallbackUrl { get; set; }

    /// <inheritdoc/>
    public long Revision { get; set; } = 1;
}
