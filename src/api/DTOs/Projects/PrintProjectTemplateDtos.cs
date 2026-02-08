using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.DTOs.Projects;

/// <summary>
/// Summary information for a project template.
/// </summary>
public record PrintProjectTemplateListDto(
    Guid Id,
    string Name,
    string? Description,
    string? Category,
    int FileCount,
    int TotalPrintCount,
    bool IsSystemTemplate,
    int SortOrder);

/// <summary>
/// Detailed information for a project template including all file entries.
/// </summary>
public record PrintProjectTemplateDetailDto(
    Guid Id,
    string Name,
    string? Description,
    string? Category,
    int DefaultPriority,
    string? DefaultNotes,
    bool IsSystemTemplate,
    int SortOrder,
    IReadOnlyList<PrintProjectTemplateFileDto> Files,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// A file entry within a project template.
/// </summary>
public record PrintProjectTemplateFileDto(
    Guid Id,
    string Name,
    string? FileNamePattern,
    PrintColorRequirement ColorRequirement,
    string? MaterialRequirement,
    int PrintCount,
    int SortOrder,
    string? Notes);

/// <summary>
/// Request to create a new project template.
/// </summary>
public record CreatePrintProjectTemplateRequest(
    string Name,
    string? Description = null,
    string? Category = null,
    int DefaultPriority = 0,
    string? DefaultNotes = null,
    IReadOnlyList<CreateTemplateFileRequest>? Files = null);

/// <summary>
/// Request to add a file entry to a template.
/// </summary>
public record CreateTemplateFileRequest(
    string Name,
    string? FileNamePattern = null,
    PrintColorRequirement ColorRequirement = PrintColorRequirement.Base,
    string? MaterialRequirement = null,
    int PrintCount = 1,
    string? Notes = null);

/// <summary>
/// Request to update a project template.
/// </summary>
public record UpdatePrintProjectTemplateRequest(
    string? Name = null,
    string? Description = null,
    string? Category = null,
    int? DefaultPriority = null,
    string? DefaultNotes = null,
    int? SortOrder = null);

/// <summary>
/// Request to update a file entry within a template.
/// </summary>
public record UpdateTemplateFileRequest(
    string? Name = null,
    string? FileNamePattern = null,
    PrintColorRequirement? ColorRequirement = null,
    string? MaterialRequirement = null,
    int? PrintCount = null,
    int? SortOrder = null,
    string? Notes = null);
