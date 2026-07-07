using Farm.Infrastructure.Dtos;

namespace Farm.Infrastructure.Dtos.PrintQueue;

// ============= MAIN RESPONSE DTOs =============

/// <summary>
/// Represents a queued print job with associated G-code file and printer metadata
/// </summary>
public class QueuedPrintJobWithFileMetaDto
{
    public QueuedPrintJobDto Job { get; set; } = null!;

    public QueueGcodeFileMetaDto GcodeFile { get; set; } = null!;

    public QueuePrinterMetaDto? AssignedPrinter { get; set; }

    public DateTime? EstimatedStartTime { get; set; }

    public DateTime? EstimatedCompletionTime { get; set; }
}

/// <summary>
/// Core print job details for queue display
/// </summary>
public class QueuedPrintJobDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// G-code file ID. Nullable for history-seeded jobs where the original
    /// file may not exist in PrintFarmer's library.
    /// </summary>
    public string? GcodeFileId { get; set; }

    public string? FileName { get; set; } // Original G-code filename for display

    public string? AssignedPrinterId { get; set; }

    /// <summary>
    /// Name of the assigned printer (denormalized for display)
    /// </summary>
    public string? PrinterName { get; set; }

    /// <summary>
    /// Model name of the assigned printer (denormalized for display)
    /// </summary>
    public string? PrinterModel { get; set; }

    public string Status { get; set; } = string.Empty;

    public int Priority { get; set; }

    public int QueuePosition { get; set; }

    public decimal? RequiredNozzleDiameter { get; set; }

    public string? RequiredMaterialType { get; set; }

    public string[]? RequiredCapabilities { get; set; }

    public int? EstimatedPrintTimeSeconds { get; set; }

    public double? EstimatedFilamentUsageGrams { get; set; }

    public DateTime? ActualStartTimeUtc { get; set; }

    public DateTime? ActualEndTimeUtc { get; set; }

    public int? ActualPrintTimeSeconds { get; set; }

    public double? ActualFilamentUsageGrams { get; set; }

    public string? FailureReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime QueuedAtUtc { get; set; }

    /// <summary>
    /// Optional UTC deadline for this job.
    /// </summary>
    public DateTime? DeadlineAtUtc { get; set; }

    /// <summary>
    /// True when the job was imported from a printer history API.
    /// </summary>
    public bool WasSeededFromHistory { get; set; }

    /// <summary>
    /// Notes/comments about this print job
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Tags assigned to this job for organization
    /// </summary>
    public string[]? Tags { get; set; }

    /// <summary>
    /// Spoolman filament ID (if assigned)
    /// </summary>
    public int? SpoolmanFilamentId { get; set; }

    /// <summary>
    /// Filament name from Spoolman (denormalized for display)
    /// </summary>
    public string? FilamentName { get; set; }

    /// <summary>
    /// Filament vendor from Spoolman (denormalized for display)
    /// </summary>
    public string? FilamentVendor { get; set; }

    /// <summary>
    /// Filament color hex from Spoolman (denormalized for display)
    /// </summary>
    public string? FilamentColor { get; set; }

    /// <summary>
    /// ID of the project this job belongs to (if any)
    /// </summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// Name of the project this job belongs to (denormalized for display)
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// Estimated cost of the print job (from spool price and filament usage)
    /// </summary>
    public decimal? EstimatedCost { get; set; }

    /// <summary>
    /// Actual cost of the print job (calculated on completion)
    /// </summary>
    public decimal? ActualCost { get; set; }

    /// <summary>
    /// Total number of copies to print for this job.
    /// </summary>
    public int Copies { get; set; } = 1;

    /// <summary>
    /// Number of copies successfully completed so far.
    /// </summary>
    public int CompletedCopies { get; set; }

    /// <summary>
    /// Remaining copies to print (Copies - CompletedCopies).
    /// </summary>
    public int RemainingCopies { get; set; }

    /// <summary>
    /// Link to the project file this job was created from (if any).
    /// </summary>
    public Guid? ProjectFileId { get; set; }

    /// <summary>
    /// URL to the G-code file thumbnail image (if available)
    /// </summary>
    public string? ThumbnailUrl { get; set; }

    /// <summary>
    /// Per-toolhead filament usage records for multi-tool/MMU jobs.
    /// Empty for single-extruder jobs.
    /// </summary>
    public List<PrintJobToolheadUsageDto> ToolheadUsages { get; set; } = [];
}

/// <summary>
/// G-code file metadata for queue display
/// </summary>
public class QueueGcodeFileMetaDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty; // Original filename for display

    public string FileName { get; set; } = string.Empty; // GUID-based filename on disk

    public long FileSizeBytes { get; set; }

    public string? MaterialType { get; set; }

    public decimal? NozzleDiameter { get; set; }

    public int? EstimatedPrintTimeSeconds { get; set; }

    public int? EstimatedFilamentUsageGrams { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string? ThumbnailUrl { get; set; }
}

/// <summary>
/// Printer metadata for queue display
/// </summary>
public class QueuePrinterMetaDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public bool IsOnline { get; set; }
}

// ============= REQUEST DTOs =============

/// <summary>
/// Request to enqueue a new print job
/// </summary>
public class EnqueueQueueJobRequest
{
    public string GcodeFileId { get; set; } = null!;

    public int Priority { get; set; } = 1;

    public string? AssignedPrinterId { get; set; }

    public decimal? RequiredNozzleDiameter { get; set; }

    public string? RequiredMaterialType { get; set; }

    /// <summary>
    /// Optional UTC deadline for this job.
    /// </summary>
    public DateTime? DeadlineAtUtc { get; set; }
}

/// <summary>
/// Request to update a print job
/// </summary>
public class UpdateQueueJobRequest
{
    public int? Priority { get; set; }

    public string? AssignedPrinterId { get; set; }

    public string? Status { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>
    /// Optional UTC deadline for this job.
    /// </summary>
    public DateTime? DeadlineAtUtc { get; set; }
}

/// <summary>
/// Request to update job priority
/// </summary>
public class UpdateQueueJobPriorityRequest
{
    public required int NewPriority { get; set; }
}

/// <summary>
/// Request for bulk cancel operations
/// </summary>
public class BulkCancelQueueJobsRequest
{
    public List<string> JobIds { get; set; } = new();
}

/// <summary>
/// Request for bulk reorder operations
/// </summary>
public class BulkReorderQueueJobsRequest
{
    public List<QueueJobReorderMove> Moves { get; set; } = new();
}

/// <summary>
/// Represents a single reorder move in bulk operations
/// </summary>
public class QueueJobReorderMove
{
    public string JobId { get; set; } = null!;

    public int NewPosition { get; set; }
}

/// <summary>
/// Request to seed history from printer APIs.
/// Fetches all available history - no date filtering since the backend
/// ISupportsHistory interface doesn't support it. Deduplication prevents duplicates.
/// </summary>
public class SeedQueueHistoryRequest
{
    /// <summary>
    /// Optional list of printer IDs to seed from. If null/empty, seeds from all enabled printers.
    /// </summary>
    public List<string>? PrinterIds { get; set; }
}

// ============= RESPONSE DTOs =============

/// <summary>
/// Result of a seeded-history duplicate cleanup run. Duplicates are jobs that share
/// the same printer and the same whole-second <c>ActualStartTime</c> (mirroring the
/// harvest-time dedup guard). Only history-seeded rows are removed; native jobs are kept.
/// </summary>
public class DeduplicateHistoryResultDto
{
    /// <summary>
    /// When true, no rows were deleted; the result reports what would have been removed.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Number of duplicate groups (printer + whole-second start) that had at least one
    /// removable seeded duplicate.
    /// </summary>
    public int DuplicateGroups { get; set; }

    /// <summary>
    /// Number of seeded duplicate jobs removed (or that would be removed in a dry run).
    /// </summary>
    public int JobsRemoved { get; set; }

    /// <summary>
    /// Per-group detail of the retained job and the removed duplicates.
    /// </summary>
    public List<DeduplicateHistoryGroupDto> Groups { get; set; } = new();
}

/// <summary>
/// Detail of a single duplicate group processed by the seeded-history cleanup.
/// </summary>
public class DeduplicateHistoryGroupDto
{
    /// <summary>
    /// Effective printer the duplicate jobs belong to (source printer, else assigned printer).
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Whole-second UTC start time shared by the jobs in this group.
    /// </summary>
    public DateTime StartTimeUtc { get; set; }

    /// <summary>
    /// The job retained as the canonical record for this group.
    /// </summary>
    public Guid RetainedJobId { get; set; }

    /// <summary>
    /// The seeded duplicate jobs removed (or that would be removed in a dry run).
    /// </summary>
    public List<Guid> RemovedJobIds { get; set; } = new();
}

/// <summary>
/// Result of bulk queue operations
/// </summary>
public class QueueBulkOperationResultDto
{
    public int TotalRequested { get; set; }

    public int SuccessfulCount { get; set; }

    public int FailedCount { get; set; }

    public List<QueueOperationFailureDto> Failures { get; set; } = new();

    public DateTime CompletedAtUtc { get; set; }
}

/// <summary>
/// Details of a single operation failure
/// </summary>
public class QueueOperationFailureDto
{
    public string ItemId { get; set; } = string.Empty;

    public string ErrorCode { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;
}

/// <summary>
/// Statistics for printer models with queued jobs
/// </summary>
public class QueuePrinterModelStatsDto
{
    public string ModelName { get; set; } = string.Empty;

    public int TotalQueued { get; set; }

    public int CurrentlyPrinting { get; set; }

    public DateTime? OldestQueuedAtUtc { get; set; }

    public int AverageQueueWaitMinutes { get; set; }
}

/// <summary>
/// Overall queue statistics
/// </summary>
public class QueueStatsDto
{
    public int TotalQueued { get; set; }

    public int TotalPrinting { get; set; }

    public int TotalPaused { get; set; }

    public int AverageWaitTimeMinutes { get; set; }

    public List<QueuePrinterModelStatsDto> ByModel { get; set; } = new();

    public DateTime? EstimatedQueueCompletionUtc { get; set; }

    public DateTime? StaffedCompletionUtc { get; set; }

    public QueuePlanningAssumptionsDto Assumptions { get; set; } = new();
}

public class QueuePlanningAssumptionsDto
{
    public int WorkdayStartHourUtc { get; set; }

    public int WorkdayEndHourUtc { get; set; }

    public int BedClearMinutes { get; set; }

    public int? DefaultDeadlineHours { get; set; }

    public bool RequireDeadline { get; set; }

    public int MinimumLeadHours { get; set; }
}

/// <summary>
/// Recommendation item for high-impact queue operator actions.
/// </summary>
public class QueueRecommendationDto
{
    /// <summary>
    /// Machine-readable category key.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Short recommendation title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Operator action text shown in the dashboard.
    /// </summary>
    public string ActionText { get; set; } = string.Empty;

    /// <summary>
    /// Number of queued jobs expected to unlock after taking this action.
    /// </summary>
    public int EstimatedUnlockedJobCount { get; set; }

    /// <summary>
    /// Deterministic ranking score for ordering recommendations.
    /// </summary>
    public int PriorityScore { get; set; }
}

// ============= HISTORY DTOs (Phase 2) =============

/// <summary>
/// Paginated response for print queue history
/// </summary>
public class QueueHistoryPageDto
{
    public List<QueueHistoryEntryDto> Entries { get; set; } = new();

    public int TotalCount { get; set; }

    public int CurrentPage { get; set; }

    public int PageSize { get; set; }

    /// <summary>
    /// Statistics for the entire filtered result set (not just current page)
    /// </summary>
    public QueueHistoryStatsDto Stats { get; set; } = new();
}

/// <summary>
/// Statistics for filtered history results
/// </summary>
public class QueueHistoryStatsDto
{
    public int TotalCompleted { get; set; }

    public int TotalFailed { get; set; }

    public int TotalCancelled { get; set; }

    public int SuccessRate { get; set; }

    public int AverageDurationMinutes { get; set; }

    public long TotalPrintTimeMinutes { get; set; }
}

/// <summary>
/// Single entry in print queue history
/// </summary>
public class QueueHistoryEntryDto
{
    public string Id { get; set; } = string.Empty;

    public string JobName { get; set; } = string.Empty;

    public string PrinterName { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int CompletionPercentage { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Optional UTC deadline for this job.
    /// </summary>
    public DateTime? DeadlineAtUtc { get; set; }

    public int ActualPrintTimeSeconds { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>
    /// Per-toolhead filament usage records for multi-tool/MMU jobs.
    /// Empty for single-extruder jobs.
    /// </summary>
    public List<PrintJobToolheadUsageDto> ToolheadUsages { get; set; } = [];

    /// <summary>
    /// Filament name from Spoolman (denormalized for display, single-extruder).
    /// </summary>
    public string? FilamentName { get; set; }

    /// <summary>
    /// Filament color hex from Spoolman (denormalized for display, single-extruder).
    /// </summary>
    public string? FilamentColor { get; set; }

    /// <summary>
    /// Actual filament consumed in grams.
    /// </summary>
    public double? ActualFilamentUsageGrams { get; set; }

    /// <summary>
    /// Actual cost of the print job (calculated on completion).
    /// </summary>
    public decimal? ActualCost { get; set; }

    /// <summary>
    /// Material cost in USD (filament usage × price per gram). Used as a display
    /// fallback for jobs that have no per-toolhead usage records (e.g. history-seeded jobs).
    /// </summary>
    public decimal? MaterialCostUsd { get; set; }

    /// <summary>
    /// Total cost in USD (material + energy + machine time + labor). Provided for
    /// display context on jobs without per-toolhead usage records.
    /// </summary>
    public decimal? TotalCostUsd { get; set; }

    /// <summary>
    /// True when the cost figures are an estimate rather than backed by real
    /// associated Spoolman spools. Cost is treated as actual only when every
    /// contributing material usage has an associated spool: for jobs with
    /// per-toolhead usages, all usages must be spool-backed; otherwise the job
    /// itself must have an associated spool. Any missing spool means at least
    /// part of the cost was derived from filament-level or default/material
    /// pricing, so the figure is flagged as an estimate. History-seeded jobs
    /// (filament weight only, no spool) are always estimated.
    /// </summary>
    public bool CostIsEstimated { get; set; }

    /// <summary>
    /// Tags associated with the print job (auto-generated and manual).
    /// </summary>
    public List<TagDto> Tags { get; set; } = [];
}

// ============= REQUEST DTOs (Phase 3) =============

/// <summary>
/// Request to update job details (name, priority, notes, tags, etc.)
/// </summary>
public class UpdateJobDetailsRequest
{
    public string? Name { get; set; }

    public int? Priority { get; set; }

    public string? Notes { get; set; }

    public string[]? Tags { get; set; }

    public string? RequiredMaterialType { get; set; }

    public decimal? RequiredNozzleDiameter { get; set; }

    /// <summary>
    /// Spoolman filament ID. Set to 0 to clear.
    /// </summary>
    public int? SpoolmanFilamentId { get; set; }

    public string? FilamentName { get; set; }

    public string? FilamentVendor { get; set; }

    public string? FilamentColor { get; set; }

    /// <summary>
    /// Number of copies to print. Must be >= CompletedCopies.
    /// </summary>
    public int? Copies { get; set; }

    /// <summary>
    /// Optional UTC deadline for this job.
    /// </summary>
    public DateTime? DeadlineAtUtc { get; set; }
}

/// <summary>
/// Request to update job notes
/// </summary>
public class UpdateJobNotesRequest
{
    public string? Notes { get; set; }
}

// ============= TIMELINE & ANALYTICS DTOs (Phase 3C) =============

/// <summary>
/// Represents a single event on the job timeline
/// </summary>
public class TimelineEventDto
{
    public string JobId { get; set; } = string.Empty;

    public string JobName { get; set; } = string.Empty;

    public string PrinterName { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty; // Queued, Printing, Paused, Completed, Failed, Cancelled

    public DateTime EnteredAtUtc { get; set; }

    public DateTime? ExitedAtUtc { get; set; }

    public int? DurationSeconds { get; set; }

    public int? EstimatedDurationSeconds { get; set; }

    public decimal? VariancePercent { get; set; }
}

/// <summary>
/// Represents a single state transition in a job's history
/// </summary>
public class StateTransitionDto
{
    public string FromState { get; set; } = string.Empty;

    public string ToState { get; set; } = string.Empty;

    public DateTime TransitionedAtUtc { get; set; }

    public int? DurationInStateSeconds { get; set; }

    public string? Notes { get; set; }
}

/// <summary>
/// Complete state history for a single job
/// </summary>
public class JobStateHistoryDto
{
    public string JobId { get; set; } = string.Empty;

    public string JobName { get; set; } = string.Empty;

    public List<StateTransitionDto> Transitions { get; set; } = new();

    public int? TotalDurationSeconds { get; set; }

    public int? EstimatedDurationSeconds { get; set; }

    public decimal? VariancePercent { get; set; }
}

/// <summary>
/// Duration comparison data for a single printer or aggregate
/// </summary>
public class DurationStatsDto
{
    public string? PrinterId { get; set; }

    public string? PrinterName { get; set; }

    public int TotalJobs { get; set; }

    public double AverageEstimatedSeconds { get; set; }

    public double AverageActualSeconds { get; set; }

    public double AccuracyPercent { get; set; } // 0-100

    public double VariancePercent { get; set; } // -100 to +100

    public int MinActualSeconds { get; set; }

    public int MaxActualSeconds { get; set; }
}

/// <summary>
/// Aggregate duration analytics across all jobs or filtered set
/// </summary>
public class DurationAnalyticsDto
{
    public int TotalJobs { get; set; }

    public double AverageEstimatedSeconds { get; set; }

    public double AverageActualSeconds { get; set; }

    public double OverallAccuracyPercent { get; set; } // 0-100

    public double OverallVariancePercent { get; set; } // -100 to +100

    public Dictionary<string, DurationStatsDto> ByPrinter { get; set; } = new();

    public List<DurationStatsDto> TopPerformers { get; set; } = new(); // Most accurate printers

    public List<DurationStatsDto> NeedsAttention { get; set; } = new(); // Least accurate printers
}
