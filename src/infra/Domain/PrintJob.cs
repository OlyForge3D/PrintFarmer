using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Domain;

// Job Queue System
public class PrintJob
{
    public Guid Id { get; set; }

    /// <summary>
    /// Optimistic concurrency token for EF Core.
    /// Critical for job queue operations where multiple processes may claim jobs.
    /// </summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public string Name { get; set; } = string.Empty; // Display name for the job

    /// <summary>
    /// The G-code file for this job. Nullable for history-seeded jobs where the
    /// original file may not exist in PrintFarmer's library.
    /// </summary>
    public Guid? GcodeFileId { get; set; }

    public GcodeFile? GcodeFile { get; set; }

    public Guid? AssignedPrinterId { get; set; }

    public Printer? AssignedPrinter { get; set; }

    public PrintJobStatus Status { get; set; }

    public int Priority { get; set; } // Higher = more important

    public int QueuePosition { get; set; }

    public decimal? RequiredNozzleDiameter { get; set; }

    public string? RequiredMaterialType { get; set; }

    public string[]? RequiredCapabilities { get; set; } // JSON array of required capabilities

    public TimeSpan? EstimatedPrintTime { get; set; }

    public double? EstimatedFilamentUsage { get; set; }

    public DateTime? ActualStartTime { get; set; }

    public DateTime? ActualEndTime { get; set; }

    public TimeSpan? ActualPrintTime { get; set; }

    public double? ActualFilamentUsage { get; set; }

    /// <summary>
    /// Estimated cost of the print job in the user's currency, calculated from
    /// spool price and estimated filament usage. Populated at queue time if
    /// Spoolman spool data is available.
    /// </summary>
    public decimal? EstimatedCost { get; set; }

    /// <summary>
    /// Actual cost of the print job in the user's currency, calculated from
    /// spool price and actual filament usage. Populated on job completion.
    /// </summary>
    public decimal? ActualCost { get; set; }

    public string? FailureReason { get; set; }

    public Guid[]? PreferredPrinterIds { get; set; } // JSON array of preferred printer IDs

    public Guid[]? ExcludedPrinterIds { get; set; } // JSON array of excluded printer IDs

    public string? Notes { get; set; } // Job notes/comments (max 500 characters)

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime QueuedAt { get; set; }

    // History Seeding: Track external job source for deduplication

    /// <summary>
    /// External job ID from the printer backend (e.g., Moonraker's JobId).
    /// Used for deduplication when seeding history from printers.
    /// </summary>
    public string? ExternalJobId { get; set; }

    /// <summary>
    /// The printer that originally reported this job during history seeding.
    /// Combined with ExternalJobId forms a unique composite key for deduplication.
    /// </summary>
    public Guid? SourcePrinterId { get; set; }

    /// <summary>
    /// Flag indicating this job was seeded from printer history rather than
    /// created through PrintFarmer's job queue.
    /// </summary>
    public bool WasSeededFromHistory { get; set; }

    // Project tracking: link job to its source project and filament assignment

    /// <summary>
    /// ID of the project this job was queued from (if any).
    /// </summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// Denormalized project name for display without a join.
    /// </summary>
    [MaxLength(255)]
    public string? ProjectName { get; set; }

    /// <summary>
    /// Spoolman filament ID assigned via the project file (if any).
    /// </summary>
    public int? SpoolmanFilamentId { get; set; }

    /// <summary>
    /// Denormalized filament display name (e.g., "PolyTerra PLA Charcoal Black").
    /// </summary>
    [MaxLength(255)]
    public string? FilamentName { get; set; }

    /// <summary>
    /// Denormalized filament vendor (e.g., "Polymaker").
    /// </summary>
    [MaxLength(128)]
    public string? FilamentVendor { get; set; }

    /// <summary>
    /// Denormalized filament color hex (e.g., "#1A1A1A").
    /// </summary>
    [MaxLength(32)]
    public string? FilamentColor { get; set; }

    // Phase 3C: Timeline tracking
    public ICollection<JobStateHistory> StateHistory { get; } = new List<JobStateHistory>();

    // Phase 4.1: Job Scheduling (one-to-one relationship)
    public JobSchedule? Schedule { get; set; }

    // Phase 4.2: Completion Statistics (one-to-one relationship)
    public PrintJobStatistics? Statistics { get; set; }

    // Phase 4.4: Job Retry History

    /// <summary>
    /// Retry history where THIS job is the original failed job
    /// </summary>
    public ICollection<JobRetry> RetriesAsOriginal { get; } = new List<JobRetry>();

    /// <summary>
    /// Retry history where THIS job is a retry attempt (reference to original in JobRetry.OriginalJobId)
    /// </summary>
    public ICollection<JobRetry> RetriesAsAttempt { get; } = new List<JobRetry>();
}
