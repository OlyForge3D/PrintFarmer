using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

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
    public interface IModel3DFileService
    {
        /// <summary>
        /// Retrieves all valid 3D models in a flat list.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Read-only list of all Model3DDto entities, ordered by upload date</returns>
        Task<IReadOnlyList<Model3DDto>> ListModelsAsync(CancellationToken ct);

        /// <summary>
        /// Lists all 3D model folders recursively for building a folder tree structure.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Flat list of all folders in the models directory hierarchy</returns>
        /// <remarks>
        /// Returns all folders without pagination or file information. Intended for tree-view UI components
        /// that need the complete folder hierarchy for navigation. Folders are returned in path order.
        /// </remarks>
        Task<List<Model3DEntryDto>> ListAllFoldersAsync(CancellationToken ct);

        /// <summary>
        /// Queries models with comprehensive filtering, sorting, and pagination.
        /// This is the efficient query method that performs all operations at database level.
        /// Provides parity with GcodeFilesService.QueryAsync for consistent API patterns.
        /// </summary>
        /// <param name="path">Optional directory path filter; null for all directories</param>
        /// <param name="sortBy">Sort field: "name", "size", or "date"</param>
        /// <param name="sortOrder">Sort order: "asc" or "desc"</param>
        /// <param name="search">Optional search query for file name (case-insensitive)</param>
        /// <param name="page">Page number (1-based)</param>
        /// <param name="pageSize">Number of items per page</param>
        /// <param name="tagIds">Optional array of tag IDs for filtering (AND logic)</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Response containing paginated models, totals, and metadata</returns>
        Task<Model3DListResponse> QueryAsync(
            string? path,
            string? sortBy,
            string? sortOrder,
            string? search,
            int page,
            int pageSize,
            Guid[]? tagIds,
            CancellationToken ct);

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

        /// <summary>
        /// Gets or creates a Folder entity for the given directory path and type
        /// </summary>
        /// <param name="directoryPath">The virtual directory path (e.g., "/", "/subfolder")</param>
        /// <param name="folderType">The folder type: "models" or "gcode"</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>The FolderNode entity, either existing or newly created</returns>
        Task<FolderNode> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken ct);

        /// <summary>
        /// Moves a 3D model file to a different virtual folder by updating its database folder reference.
        /// </summary>
        /// <param name="modelId">GUID of the model to move</param>
        /// <param name="targetFolderPath">Virtual path of the destination folder</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if the model was successfully moved; false if model was not found</returns>
        /// <remarks>
        /// This is a virtual move operation that only updates the model's FolderId reference in the database.
        /// The physical file remains in its original location on disk with its GUID-based filename.
        /// Target folder is created automatically if it doesn't exist.
        /// </remarks>
        Task<bool> MoveToFolderAsync(Guid modelId, string targetFolderPath, CancellationToken ct);

        /// <summary>
        /// Downloads a file from the model storage directory by relative path.
        /// Unified with Gcode download endpoint for consistent thumbnail serving.
        /// </summary>
        /// <param name="path">Relative path to the file within model storage directory</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Tuple of (file bytes, safe filename) if found, otherwise null</returns>
        /// <remarks>
        /// This method serves both model files and thumbnails using path-based lookups.
        /// Path validation is performed internally to prevent directory traversal attacks.
        /// </remarks>
        Task<(byte[] bytes, string fileName)?> DownloadFileAsync(string path, CancellationToken ct);
    }
}
