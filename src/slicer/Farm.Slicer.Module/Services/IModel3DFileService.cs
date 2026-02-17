using Farm.Slicer.Module.Dtos;
using Microsoft.AspNetCore.Http;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Service interface for 3D model file management and operations.
/// </summary>
public interface IModel3DFileService
{
    /// <summary>Retrieves all valid 3D models in a flat list.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Model3DDto>> ListModelsAsync(CancellationToken ct);

    /// <summary>Lists all 3D model folders recursively for building a folder tree structure.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<List<Model3DEntryDto>> ListAllFoldersAsync(CancellationToken ct);

    /// <summary>
    /// Queries models with comprehensive filtering, sorting, and pagination.
    /// </summary>
    /// <param name="path">Optional directory path filter.</param>
    /// <param name="sortBy">Sort field: "name", "size", or "date".</param>
    /// <param name="sortOrder">Sort order: "asc" or "desc".</param>
    /// <param name="search">Optional search query for file name.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="tagIds">Optional tag IDs for filtering (AND logic).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Model3DListResponse> QueryAsync(
        string? path,
        string? sortBy,
        string? sortOrder,
        string? search,
        int page,
        int pageSize,
        Guid[]? tagIds,
        CancellationToken ct);

    /// <summary>Retrieves a single model by its unique identifier.</summary>
    /// <param name="id">The model's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Model3DDto?> GetModelAsync(Guid id, CancellationToken ct);

    /// <summary>Retrieves the file system path for a model's 3D file.</summary>
    /// <param name="id">The model's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> GetModelFilePathAsync(Guid id, CancellationToken ct);

    /// <summary>Retrieves the file system path for a model's thumbnail image.</summary>
    /// <param name="id">The model's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> GetModelThumbnailPathAsync(Guid id, CancellationToken ct);

    /// <summary>Deletes a model and its associated files.</summary>
    /// <param name="id">The model's unique identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteModelAsync(Guid id, CancellationToken ct);

    /// <summary>Validates a model file without uploading it.</summary>
    /// <param name="modelFile">The model file to validate.</param>
    Model3DValidationResultDto ValidateModel(IFormFile modelFile);

    /// <summary>Uploads and processes a 3D model file.</summary>
    /// <param name="modelFile">The model file to upload.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Model3DUploadResultDto> UploadModelAsync(IFormFile modelFile, CancellationToken ct);

    /// <summary>Gets or creates a folder for the given directory path and type.</summary>
    /// <param name="directoryPath">The virtual directory path.</param>
    /// <param name="folderType">The folder type: "models" or "gcode".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The folder identifier.</returns>
    Task<Guid> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken ct);

    /// <summary>Moves a 3D model file to a different virtual folder.</summary>
    /// <param name="modelId">The model identifier.</param>
    /// <param name="targetFolderPath">Virtual path of the destination folder.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the model was successfully moved.</returns>
    Task<bool> MoveToFolderAsync(Guid modelId, string targetFolderPath, CancellationToken ct);

    /// <summary>Downloads a file from the model storage directory by relative path.</summary>
    /// <param name="path">Relative path to the file within model storage.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<(byte[] Bytes, string FileName)?> DownloadFileAsync(string path, CancellationToken ct);
}
