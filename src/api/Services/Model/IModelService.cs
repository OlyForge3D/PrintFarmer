using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Model
{
    /// <summary>
    /// Service interface for 3D model file management and operations.
    /// Provides business logic for:
    /// - Listing models (flat list or hierarchical with folders)
    /// - File validation and upload
    /// - Metadata extraction and thumbnail generation
    /// - File access and deletion
    /// </summary>
    public interface IModelService
    {
        /// <summary>
        /// Retrieves all valid 3D models in a flat list.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Read-only list of all Model3DDto entities, ordered by upload date</returns>
        Task<IReadOnlyList<Model3DDto>> ListModelsAsync(CancellationToken ct);

        /// <summary>
        /// Retrieves models and subdirectories from a specific folder path with hierarchical support.
        /// Enables folder-based browsing of 3D models with pagination and searching.
        /// Results include both files and directories, sorted with directories first.
        /// </summary>
        /// <param name="path">Virtual folder path (e.g., "/mechanical/gears"); null or "/" for root</param>
        /// <param name="sortBy">Sort field: "name", "size", or "date" (default: "name")</param>
        /// <param name="sortOrder">"asc" for ascending or "desc" for descending (default: "asc")</param>
        /// <param name="search">Optional search term to filter files and directories by name</param>
        /// <param name="page">Page number for pagination (1-based; default: 1)</param>
        /// <param name="pageSize">Results per page (max 500; default: 20)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Model3DListResponse with paginated results, totals, and directory structure</returns>
        Task<Model3DListResponse> ListModelsWithHierarchyAsync(string? path, string? sortBy, string? sortOrder, string? search, int page, int pageSize, CancellationToken ct);

        /// <summary>
        /// Retrieves a single model by its unique identifier.
        /// </summary>
        /// <param name="id">The model's unique identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>The Model3DDto if found, otherwise null</returns>
        Task<Model3DDto?> GetModelAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Retrieves the file system path for a specific model's 3D file.
        /// Used for file downloads and processing.
        /// </summary>
        /// <param name="id">The model's unique identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>The full file path if model exists, otherwise null</returns>
        Task<string?> GetModelFilePathAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Retrieves the file system path for a model's thumbnail image.
        /// Used for thumbnail downloads and UI display.
        /// </summary>
        /// <param name="id">The model's unique identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>The full thumbnail path if it exists, otherwise null</returns>
        Task<string?> GetModelThumbnailPathAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Deletes a model and its associated files (model file and thumbnail).
        /// Removes the database record and cleans up file system storage.
        /// </summary>
        /// <param name="id">The model's unique identifier (GUID)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <exception cref="KeyNotFoundException">Thrown if model with given ID is not found</exception>
        Task DeleteModelAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Validates a model file without uploading it.
        /// Checks file format, performs basic STL/3MF validation, and verifies file integrity.
        /// </summary>
        /// <param name="modelFile">The model file to validate (IFormFile from HTTP request)</param>
        /// <returns>Validation result with issues list and pass/fail status</returns>
        /// <exception cref="ArgumentException">Thrown if model file is null or empty</exception>
        Model3DValidationResultDto ValidateModel(IFormFile modelFile);

        /// <summary>
        /// Uploads and processes a 3D model file.
        /// Performs file validation, hash computation, deduplication checking, metadata extraction, 
        /// and thumbnail generation. Returns immediately; processing continues in background.
        /// </summary>
        /// <param name="modelFile">The model file to upload (IFormFile from HTTP request)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Model3DUploadResultDto with file info and upload status</returns>
        /// <exception cref="ArgumentException">Thrown if validation fails</exception>
        Task<Model3DUploadResultDto> UploadModelAsync(IFormFile modelFile, CancellationToken ct);
    }
}
