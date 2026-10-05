using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Dtos.Projects;

/// <summary>
/// Summary DTO for displaying projects in lists.
/// </summary>
public record PrintProjectListDto(
    Guid Id,
    string Name,
    string? Description,
    PrintProjectStatus Status,
    int Priority,
    DateTime? DueDate,
    int TotalFiles,
    int CompletedFiles,
    int TotalPrints,
    int CompletedPrints,
    decimal? EstimatedTotalCost,
    decimal? CompletedCost,
    DateTime CreatedAt,
    DateTime? CompletedAt)
{
    /// <summary>
    /// Progress percentage (0-100) based on completed prints.
    /// </summary>
    public int ProgressPercent => TotalPrints > 0 ? (int)Math.Round(100.0 * CompletedPrints / TotalPrints) : 0;
}

/// <summary>
/// Detailed DTO for single project view with all files.
/// </summary>
public record PrintProjectDetailDto(
    Guid Id,
    string Name,
    string? Description,
    PrintProjectStatus Status,
    int Priority,
    DateTime? DueDate,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? CompletedAt,
    IReadOnlyList<PrintProjectFileDto> Files)
{
    /// <summary>
    /// Total number of prints required across all files.
    /// </summary>
    public int TotalPrints => Files.Sum(f => f.PrintCount);

    /// <summary>
    /// Total number of prints completed across all files.
    /// </summary>
    public int CompletedPrints => Files.Sum(f => f.PrintedCount);

    /// <summary>
    /// Progress percentage (0-100) based on completed prints.
    /// </summary>
    public int ProgressPercent => TotalPrints > 0 ? (int)Math.Round(100.0 * CompletedPrints / TotalPrints) : 0;

    /// <summary>
    /// Estimated total cost across all remaining file prints.
    /// </summary>
    public decimal? EstimatedTotalCost => Files.Any(f => f.EstimatedFileCost.HasValue)
        ? Files.Sum(f => f.EstimatedFileCost ?? 0m)
        : null;

    /// <summary>
    /// Actual cost from completed print jobs.
    /// </summary>
    public decimal? CompletedCost { get; init; }
}

/// <summary>
/// DTO for a file within a project.
/// </summary>
public record PrintProjectFileDto(
    Guid Id,
    Guid GcodeFileId,
    string FileName,
    string? ThumbnailUrl,
    int? SpoolmanFilamentId,
    string? MaterialRequirement,
    int PrintCount,
    int PrintedCount,
    PrintProjectFileStatus Status,
    int SortOrder,
    string? Notes,
    DateTime? LastPrintedAt,
    Guid? LastPrintJobId,

    // Gcode metadata for time/material estimation
    double? EstimatedPrintTimeMinutes = null,
    double? EstimatedFilamentLengthMm = null,
    double? EstimatedFilamentWeightG = null,
    string? RequiredMaterial = null,
    double? RequiredNozzleDiameter = null,
    string? ExtractedPrinterModelName = null,

    // Cost estimate per remaining copy
    decimal? EstimatedCostPerCopy = null,

    // Optional plate index and name for multi-plate 3MF models
    int? PlateIndex = null,
    string? PlateName = null)
{
    /// <summary>
    /// Whether all required prints have been completed.
    /// </summary>
    public bool IsComplete => PrintedCount >= PrintCount;

    /// <summary>
    /// Remaining prints needed.
    /// </summary>
    public int RemainingPrints => Math.Max(0, PrintCount - PrintedCount);

    /// <summary>
    /// Total estimated time for remaining prints of this file (minutes).
    /// </summary>
    public double? RemainingPrintTimeMinutes => EstimatedPrintTimeMinutes.HasValue
        ? EstimatedPrintTimeMinutes.Value * RemainingPrints
        : null;

    /// <summary>
    /// Estimated total cost for all remaining copies of this file.
    /// </summary>
    public decimal? EstimatedFileCost => EstimatedCostPerCopy.HasValue
        ? EstimatedCostPerCopy.Value * RemainingPrints
        : null;
}

/// <summary>
/// Request to create a new print project.
/// </summary>
public record CreatePrintProjectRequest(
    string Name,
    string? Description = null,
    int Priority = 0,
    DateTime? DueDate = null,
    string? Notes = null,
    IReadOnlyList<AddFileToProjectRequest>? Files = null);

/// <summary>
/// Request to update an existing print project.
/// </summary>
public record UpdatePrintProjectRequest(
    string? Name = null,
    string? Description = null,
    PrintProjectStatus? Status = null,
    int? Priority = null,
    DateTime? DueDate = null,
    string? Notes = null);

/// <summary>
/// Request to add a file to a project.
/// </summary>
public record AddFileToProjectRequest(
    Guid GcodeFileId,
    int? SpoolmanFilamentId = null,
    string? MaterialRequirement = null,
    int PrintCount = 1,
    string? Notes = null,
    int? PlateIndex = null,
    string? PlateName = null);

/// <summary>
/// Request to update a file within a project.
/// </summary>
public record UpdateProjectFileRequest(
    int? SpoolmanFilamentId = null,
    string? MaterialRequirement = null,
    int? PrintCount = null,
    int? PrintedCount = null,
    PrintProjectFileStatus? Status = null,
    int? SortOrder = null,
    string? Notes = null,
    int? PlateIndex = null,
    string? PlateName = null);

/// <summary>
/// Progress summary for a project.
/// </summary>
public record PrintProjectProgressDto(
    Guid ProjectId,
    string ProjectName,
    PrintProjectStatus Status,
    int TotalFiles,
    int CompletedFiles,
    int TotalPrints,
    int CompletedPrints,
    int ProgressPercent,
    IReadOnlyList<FileProgressDto> FileProgress);

/// <summary>
/// Progress summary for a single file within a project.
/// </summary>
public record FileProgressDto(
    Guid FileId,
    string FileName,
    PrintProjectFileStatus Status,
    int PrintCount,
    int PrintedCount,
    bool IsComplete);

/// <summary>
/// Request to queue all pending files from a project to the job queue.
/// Files are auto-ordered by material type and color to minimize filament changes.
/// </summary>
public record QueueProjectRequest(
    Guid? AssignedPrinterId = null,
    bool GroupByMaterial = true,
    bool GroupByColor = true,
    int Priority = 1);

/// <summary>
/// Result of queueing a project's files.
/// </summary>
public record QueueProjectResultDto(
    Guid ProjectId,
    string ProjectName,
    int TotalJobsQueued,
    int TotalPrintsQueued,
    double? EstimatedTotalTimeMinutes,
    IReadOnlyList<QueuedProjectFileDto> QueuedFiles);

/// <summary>
/// A single file that was queued from a project.
/// </summary>
public record QueuedProjectFileDto(
    Guid ProjectFileId,
    Guid PrintJobId,
    string FileName,
    string? MaterialType,
    string? ColorHex,
    int PrintCount,
    double? EstimatedPrintTimeMinutes,
    int QueueOrder);
