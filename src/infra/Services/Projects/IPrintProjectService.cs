using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Projects;

namespace Farm.Infrastructure.Services.Projects;

/// <summary>
/// Service interface for managing print projects.
/// </summary>
public interface IPrintProjectService
{
    /// <summary>
    /// Get all projects with optional filtering.
    /// </summary>
    Task<IReadOnlyList<PrintProjectListDto>> GetProjectsAsync(
        PrintProjectStatus? status = null,
        string? search = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get a single project with all files.
    /// </summary>
    Task<PrintProjectDetailDto?> GetProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// Create a new project.
    /// </summary>
    Task<PrintProjectDetailDto> CreateProjectAsync(CreatePrintProjectRequest request, CancellationToken ct = default);

    /// <summary>
    /// Update an existing project.
    /// </summary>
    Task<PrintProjectDetailDto?> UpdateProjectAsync(Guid projectId, UpdatePrintProjectRequest request, CancellationToken ct = default);

    /// <summary>
    /// Delete a project and all its file associations.
    /// </summary>
    Task<bool> DeleteProjectAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// Add files to a project.
    /// </summary>
    Task<IReadOnlyList<PrintProjectFileDto>> AddFilesToProjectAsync(
        Guid projectId,
        IReadOnlyList<AddFileToProjectRequest> files,
        CancellationToken ct = default);

    /// <summary>
    /// Remove a file from a project.
    /// </summary>
    Task<bool> RemoveFileFromProjectAsync(Guid projectId, Guid fileId, CancellationToken ct = default);

    /// <summary>
    /// Update a file within a project (e.g., mark as printed, change color requirement).
    /// </summary>
    Task<PrintProjectFileDto?> UpdateProjectFileAsync(
        Guid projectId,
        Guid fileId,
        UpdateProjectFileRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Mark a file as printed (increment printed count).
    /// </summary>
    Task<PrintProjectFileDto?> MarkFilePrintedAsync(
        Guid projectId,
        Guid fileId,
        Guid? printJobId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get progress summary for a project.
    /// </summary>
    Task<PrintProjectProgressDto?> GetProjectProgressAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// Queue all pending files from a project to the job queue.
    /// Files are ordered by material type and color to minimize filament changes.
    /// </summary>
    Task<QueueProjectResultDto?> QueueProjectAsync(Guid projectId, QueueProjectRequest request, CancellationToken ct = default);
}
