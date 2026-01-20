namespace Farm.Web.Api.DTOs.PrintQueue;

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

    public string GcodeFileId { get; set; } = string.Empty;

    public string? AssignedPrinterId { get; set; }

    public string Status { get; set; } = string.Empty;

    public int Priority { get; set; }

    public int QueuePosition { get; set; }

    public decimal? RequiredNozzleDiameter { get; set; }

    public string? RequiredMaterialType { get; set; }

    public string[]? RequiredCapabilities { get; set; }

    public int? EstimatedPrintTimeSeconds { get; set; }

    public int? EstimatedFilamentUsageGrams { get; set; }

    public DateTime? ActualStartTimeUtc { get; set; }

    public DateTime? ActualEndTimeUtc { get; set; }

    public int? ActualPrintTimeSeconds { get; set; }

    public int? ActualFilamentUsageGrams { get; set; }

    public string? FailureReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime QueuedAtUtc { get; set; }
}

/// <summary>
/// G-code file metadata for queue display
/// </summary>
public class QueueGcodeFileMetaDto
{
    public string Id { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    public string? MaterialType { get; set; }

    public decimal? NozzleDiameter { get; set; }

    public int? EstimatedPrintTimeSeconds { get; set; }

    public int? EstimatedFilamentUsageGrams { get; set; }

    public DateTime CreatedAtUtc { get; set; }
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
/// Request to seed history from printer APIs
/// </summary>
public class SeedQueueHistoryRequest
{
    public List<string>? PrinterIds { get; set; }

    public int DaysBack { get; set; } = 30;
}

// ============= RESPONSE DTOs =============

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

    public int ActualPrintTimeSeconds { get; set; }

    public string? FailureReason { get; set; }
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
