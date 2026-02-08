using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.DTOs.Projects;

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
}

/// <summary>
/// DTO for a file within a project.
/// </summary>
public record PrintProjectFileDto(
    Guid Id,
    Guid GcodeFileId,
    string FileName,
    string? ThumbnailUrl,
    PrintColorRequirement ColorRequirement,
    string? MaterialRequirement,
    int PrintCount,
    int PrintedCount,
    PrintProjectFileStatus Status,
    int SortOrder,
    string? Notes,
    DateTime? LastPrintedAt,
    Guid? LastPrintJobId)
{
    /// <summary>
    /// Whether all required prints have been completed.
    /// </summary>
    public bool IsComplete => PrintedCount >= PrintCount;

    /// <summary>
    /// Remaining prints needed.
    /// </summary>
    public int RemainingPrints => Math.Max(0, PrintCount - PrintedCount);
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
    PrintColorRequirement ColorRequirement = PrintColorRequirement.Base,
    string? MaterialRequirement = null,
    int PrintCount = 1,
    string? Notes = null);

/// <summary>
/// Request to update a file within a project.
/// </summary>
public record UpdateProjectFileRequest(
    PrintColorRequirement? ColorRequirement = null,
    string? MaterialRequirement = null,
    int? PrintCount = null,
    int? PrintedCount = null,
    PrintProjectFileStatus? Status = null,
    int? SortOrder = null,
    string? Notes = null);

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
    PrintColorRequirement ColorRequirement,
    PrintProjectFileStatus Status,
    int PrintCount,
    int PrintedCount,
    bool IsComplete);
