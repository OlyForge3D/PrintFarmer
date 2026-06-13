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

    /// <summary>
    /// ID of the project this job was queued from.
    /// </summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// Denormalized project name for display.
    /// </summary>
    public string? ProjectName { get; set; }

    /// <summary>
    /// Spoolman filament ID from the project file assignment.
    /// </summary>
    public int? SpoolmanFilamentId { get; set; }

    /// <summary>
    /// Denormalized filament display name.
    /// </summary>
    public string? FilamentName { get; set; }

    /// <summary>
    /// Denormalized filament vendor name.
    /// </summary>
    public string? FilamentVendor { get; set; }

    /// <summary>
    /// Denormalized filament color hex.
    /// </summary>
    public string? FilamentColor { get; set; }

    /// <summary>
    /// Number of copies to print for this job.
    /// </summary>
    public int Copies { get; set; } = 1;

    /// <summary>
    /// Link to the project file this job was created from.
    /// </summary>
    public Guid? ProjectFileId { get; set; }

    /// <summary>
    /// Optional plate index from a multi-plate 3MF model.
    /// </summary>
    public int? PlateIndex { get; set; }

    /// <summary>
    /// Optional plate name from a multi-plate 3MF model.
    /// </summary>
    public string? PlateName { get; set; }
}

/// <summary>
/// Partial updates for a queued or active job (status, priority, assignment, actual metrics).
/// </summary>
#pragma warning disable SA1402 // File may only contain a single type
public class UpdatePrintJobStatusDto
#pragma warning restore SA1402 // File may only contain a single type
{
    public string? Name { get; set; }

    public PrintJobStatus? Status { get; set; }

    public PrintJobPriority? Priority { get; set; }

    public Guid? AssignedPrinterId { get; set; }

    public double? ActualFilamentUsage { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>
    /// Spoolman filament ID. Use 0 to clear the assignment.
    /// </summary>
    public int? SpoolmanFilamentId { get; set; }

    public string? FilamentName { get; set; }

    public string? FilamentVendor { get; set; }

    public string? FilamentColor { get; set; }
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

    /// <summary>
    /// G-code file ID. Nullable for history-seeded jobs where the original
    /// file may not exist in PrintFarmer's library.
    /// </summary>
    public Guid? GcodeFileId { get; set; }

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

    public int? SpoolmanFilamentId { get; set; }

    public string? FilamentName { get; set; }

    public string? FilamentVendor { get; set; }

    public string? FilamentColor { get; set; }

    public decimal? EstimatedCost { get; set; }

    public decimal? ActualCost { get; set; }

    public int Copies { get; set; } = 1;

    public int CompletedCopies { get; set; }

    public int RemainingCopies { get; set; }

    public Guid? ProjectFileId { get; set; }

    /// <summary>
    /// Optional plate index from a multi-plate 3MF model.
    /// </summary>
    public int? PlateIndex { get; set; }

    /// <summary>
    /// Optional plate name from a multi-plate 3MF model.
    /// </summary>
    public string? PlateName { get; set; }

    /// <summary>
    /// Per-toolhead filament usage records for multi-tool/MMU jobs.
    /// Empty for single-extruder jobs.
    /// </summary>
    public List<PrintJobToolheadUsageDto> ToolheadUsages { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
