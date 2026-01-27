namespace Farm.Infrastructure;

// Queue Management DTOs
/// <summary>
/// Aggregate queue metrics plus recent jobs for dashboard usage.
/// </summary>
public record QueueStatusDto(
    int TotalJobs,
    int QueuedJobs,
    int ActiveJobs,
    int CompletedJobs,
    int FailedJobs,
    PrintJobDto[] RecentJobs,
    PrinterCapabilitiesDto[] AvailablePrinters);

/// <summary>
/// Result of attempting to auto-assign a queued job to a printer.
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public record QueueAssignmentResultDto(
#pragma warning restore SA1402 // File may only contain a single type
    bool Success,
    string Message,
    Guid? AssignedPrinterId = null,
    string? AssignedPrinterName = null,
    string[]? MissingCapabilities = null,
    string[]? ConflictingRequirements = null);

// Queue Management DTOs
#pragma warning disable SA1402 // File may only contain a single type
public class QueueOverviewDto
#pragma warning restore SA1402 // File may only contain a single type
{
    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public string PrinterModel { get; set; } = string.Empty;

    /// <summary>
    /// Slicer-specific model names that map to this printer's model (e.g., "COREONEL", "MK4IS").
    /// Used for matching G-code files to compatible printers when the file contains raw slicer names.
    /// </summary>
    public List<string>? ModelAliases { get; set; }

    public bool IsAvailable { get; set; }

    public int QueuedJobsCount { get; set; }

    public Guid? CurrentJobId { get; set; }

    public string? CurrentJobName { get; set; }

    public DateTime? EstimatedCompletionTime { get; set; }

    public double? NozzleDiameter { get; set; }

    public List<string>? SupportedMaterials { get; set; }
}

#pragma warning disable SA1402 // File may only contain a single type
public class UpdateJobPriorityDto
#pragma warning restore SA1402 // File may only contain a single type
{
    public int Priority { get; set; }
}

/// <summary>
/// Request payload to enqueue a new job referencing an existing G-code file.
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class QueuePrintJobDto
#pragma warning restore SA1402 // File may only contain a single type
{
    public Guid GcodeFileId { get; set; }

    public Guid? AssignedPrinterId { get; set; } // If null, auto-assign to best available printer

    public PrintJobPriority Priority { get; set; } = PrintJobPriority.Normal;

    public decimal? RequiredNozzleDiameter { get; set; }

    public string? RequiredMaterialType { get; set; }

    /// <summary>
    /// Required printer model name or slicer alias (e.g., "QIDI X-Plus 4", "COREONEL").
    /// Used for auto-assign to filter printers by model compatibility.
    /// </summary>
    public string? RequiredPrinterModel { get; set; }
}

/// <summary>
/// Partial updates for a queued or active job (status, priority, assignment, actual metrics).
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class UpdatePrintJobStatusDto
#pragma warning restore SA1402 // File may only contain a single type
{
    public PrintJobStatus? Status { get; set; }

    public PrintJobPriority? Priority { get; set; }

    public Guid? AssignedPrinterId { get; set; }

    public double? ActualFilamentUsage { get; set; }

    public string? FailureReason { get; set; }
}

/// <summary>
/// Batch reordering request specifying new queue positions.
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class ReorderQueueDto
#pragma warning restore SA1402 // File may only contain a single type
{
    public JobOrderDto[] JobOrder { get; set; } = [];
}

/// <summary>
/// New ordering metadata for a single job.
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class JobOrderDto
#pragma warning restore SA1402 // File may only contain a single type
{
    public Guid JobId { get; set; }

    public int Position { get; set; }
}

/// <summary>
/// Queue-focused view of a print job used by management endpoints.
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class JobQueuePrintJobDto
#pragma warning restore SA1402 // File may only contain a single type
{
    public Guid Id { get; set; }

    public Guid GcodeFileId { get; set; }

    public string GcodeFileName { get; set; } = string.Empty;

    public Guid? AssignedPrinterId { get; set; }

    public string AssignedPrinterName { get; set; } = string.Empty;

    public PrintJobStatus? Status { get; set; }

    public int Priority { get; set; }

    public int QueuePosition { get; set; }

    public decimal? RequiredNozzleDiameter { get; set; }

    public string? RequiredMaterialType { get; set; }

    public TimeSpan? EstimatedPrintTime { get; set; }

    public double? EstimatedFilamentUsage { get; set; }

    public DateTime? ActualStartTime { get; set; }

    public DateTime? ActualEndTime { get; set; }

    public TimeSpan? ActualPrintTime { get; set; }

    public double? ActualFilamentUsage { get; set; }

    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
