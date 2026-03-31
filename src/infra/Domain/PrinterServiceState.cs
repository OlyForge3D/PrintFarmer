using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Background-service-managed state for a printer.
/// Isolated from the main Printer row to prevent xmin concurrency conflicts
/// when background services write timestamps while users edit printer config.
/// </summary>
public class PrinterServiceState
{
    public Guid PrinterId { get; set; }

    public Printer Printer { get; set; } = null!;

    /// <summary>Most recent history job timestamp imported. Used for incremental seeding.</summary>
    public DateTime? LastHistorySeedUtc { get; set; }

    /// <summary>When the catalog model template was last applied.</summary>
    public DateTime? LastModelSyncAt { get; set; }

    /// <summary>When hardware capabilities were last updated.</summary>
    public DateTime LastCapabilityUpdate { get; set; } = DateTime.UtcNow;

    /// <summary>Internal Obico ML server assignment.</summary>
    public Guid? ObicoServerId { get; set; }

    public ObicoServer? ObicoServer { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }
}
