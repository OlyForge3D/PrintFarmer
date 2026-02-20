using Farm.Infrastructure.Dtos.Projects;

namespace Farm.Infrastructure.Services.Projects;

/// <summary>
/// Service interface for managing print project templates.
/// </summary>
public interface IPrintProjectTemplateService
{
    /// <summary>
    /// Get all project templates with optional filtering.
    /// </summary>
    Task<IReadOnlyList<PrintProjectTemplateListDto>> GetTemplatesAsync(
        string? category = null,
        string? search = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get a single template with all file entries.
    /// </summary>
    Task<PrintProjectTemplateDetailDto?> GetTemplateAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>
    /// Create a new project template.
    /// </summary>
    Task<PrintProjectTemplateDetailDto> CreateTemplateAsync(CreatePrintProjectTemplateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Update an existing template.
    /// </summary>
    Task<PrintProjectTemplateDetailDto?> UpdateTemplateAsync(Guid templateId, UpdatePrintProjectTemplateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Delete a template (unless it's a system template).
    /// </summary>
    Task<bool> DeleteTemplateAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>
    /// Add a file entry to a template.
    /// </summary>
    Task<PrintProjectTemplateFileDto?> AddFileToTemplateAsync(Guid templateId, CreateTemplateFileRequest request, CancellationToken ct = default);

    /// <summary>
    /// Remove a file entry from a template.
    /// </summary>
    Task<bool> RemoveFileFromTemplateAsync(Guid templateId, Guid fileId, CancellationToken ct = default);

    /// <summary>
    /// Update a file entry within a template.
    /// </summary>
    Task<PrintProjectTemplateFileDto?> UpdateTemplateFileAsync(Guid templateId, Guid fileId, UpdateTemplateFileRequest request, CancellationToken ct = default);

    /// <summary>
    /// Get all distinct template categories.
    /// </summary>
    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default);
}
