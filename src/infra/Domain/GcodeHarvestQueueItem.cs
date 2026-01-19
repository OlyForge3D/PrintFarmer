using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

/// <summary>/// Queue item for G-code harvest operations. Decouples the API request from the background processing.
/// Allows multiple harvest requests to be queued and processed sequentially or with priority.
/// </summary>
public class GcodeHarvestQueueItem
{
    public Guid Id { get; set; }

    public Guid PrinterId { get; set; }

    public DateTime QueuedAt { get; set; }

    public DateTime? ProcessingStartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public int Priority { get; set; } = 0; // Higher = process sooner

    /// <summary>
    /// Current status of the queue item.
    /// </summary>
    public GcodeHarvestQueueItemStatus Status { get; set; } = GcodeHarvestQueueItemStatus.Pending;

    /// <summary>
    /// Serialized StartGcodeHarvestDto parameters as JSON for deferred processing.
    /// </summary>
    public string Parameters { get; set; } = string.Empty;

    /// <summary>
    /// Error message if processing failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Error details for debugging (stack trace, additional context).
    /// </summary>
    public string? ErrorDetails { get; set; }

    // Results cached after completion
    public int FilesFound { get; set; }

    public int FilesAdded { get; set; }

    public int FilesSkipped { get; set; }

    public int FilesErrored { get; set; }

    // Navigation
    public Printer? Printer { get; set; }
}
