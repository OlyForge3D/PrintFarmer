using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Services.FileManagement;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Services.Gcode
{
    /// <summary>
    /// Unified service for managing G-code files with both file browser and library operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This service provides a consolidated interface for all G-code file operations, combining the previously
    /// separate file browser (directory-based) and library (metadata-based) functionality into a single,
    /// cohesive API. This consolidation reduces code duplication and provides a single source of truth for
    /// G-code file management.
    /// </para>
    /// <para>
    /// <strong>File Browser Operations:</strong>
    /// Handle virtual folder hierarchies, directory navigation, pagination, and sorting. Physical files are stored
    /// in a flat directory structure with GUID-based names, while virtual folders exist only in the database
    /// for organizational purposes.
    /// </para>
    /// <para>
    /// <strong>Library Operations:</strong>
    /// Provide metadata-driven queries, filtering by material, nozzle diameter, and search terms. Support
    /// full CRUD operations on G-code files with thumbnail generation and management.
    /// </para>
    /// <para>
    /// <strong>Architecture:</strong>
    /// Uses a virtual folder pattern where file and folder operations are tracked in the database but
    /// files are physically stored in a single flat directory. Move operations update database references
    /// without moving physical files, providing efficient organization without filesystem overhead.
    /// </para>
    /// </remarks>
    public interface IGcodeFilesService
    {
        #region File Browser and Directory Operations

        /// <summary>
        /// Efficient query endpoint that pushes all filtering, sorting, and pagination to the database.
        /// Supports comprehensive filtering including path, search, printer model, printer, and harvest.
        /// </summary>
        /// <param name="path">Virtual directory path. Null/empty returns all files. Non-null returns files in that directory only.</param>
        /// <param name="sortBy">Sort field: 'name', 'size', or 'date'.</param>
        /// <param name="sortOrder">Sort order: 'asc' or 'desc'.</param>
        /// <param name="search">Optional search term for file names.</param>
        /// <param name="page">Page number (1-based).</param>
        /// <param name="pageSize">Page size (1-500).</param>
        /// <param name="tagIds">Optional array of tag IDs for filtering (AND logic - file must have all tags)</param>
        /// <param name="printerModelId">Optional filter by printer model ID</param>
        /// <param name="printerId">Optional filter by source printer ID</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Paginated response with file entries, totals, and metadata</returns>
        Task<GcodeFileListResponse> QueryAsync(
            string? path,
            string? sortBy,
            string? sortOrder,
            string? search,
            int page,
            int pageSize,
            Guid[]? tagIds,
            Guid? printerModelId,
            Guid? printerId,
            CancellationToken ct);

        /// <summary>
        /// Lists G-code files and subdirectories within a virtual path, organized in a hierarchical structure.
        /// </summary>
        /// <param name="path">Virtual path to browse. Null or empty defaults to root.</param>
        /// <param name="sortBy">Sort field: 'name', 'size', or 'date'.</param>
        /// <param name="sortOrder">Sort order: 'asc' or 'desc'.</param>
        /// <param name="search">Optional search term for filename filtering.</param>
        /// <param name="page">Page number for pagination (1-based).</param>
        /// <param name="pageSize">Items per page (1-500 range, default 100).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Paginated response with files for the specified path.</returns>
        /// <remarks>
        /// Returns only files in the specified directory. For folder tree structure, use ListAllFoldersAsync endpoint.
        /// </remarks>
        Task<GcodeFileListResponse> ListFilesWithHierarchyAsync(string? path, string? sortBy, string? sortOrder, string? search, int page, int pageSize, CancellationToken ct);

        /// <summary>
        /// Lists all G-code folders recursively for building a folder tree structure.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Flat list of all folders in the gcode directory hierarchy.</returns>
        /// <remarks>
        /// Returns all folders without pagination or file information. Intended for tree-view UI components
        /// that need the complete folder hierarchy for navigation. Folders are returned in path order.
        /// </remarks>
        Task<List<GcodeFileEntryDto>> ListAllFoldersAsync(CancellationToken ct);

        /// <summary>
        /// Uploads a single G-code file to the specified virtual directory with quota validation.
        /// </summary>
        /// <param name="path">Virtual directory path where file will be stored (e.g., '/prints'). Null defaults to root.</param>
        /// <param name="file">The uploaded file from an HTTP request.</param>
        /// <param name="uploadSettings">Configuration settings for upload (max file size, allowed extensions, etc.).</param>
        /// <param name="quotaService">Service for validating upload quotas and storage limits.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>DTO containing uploaded file metadata including folder reference and file information.</returns>
        /// <exception cref="InvalidOperationException">Thrown if file exceeds size limits or quota is exceeded.</exception>
        /// <remarks>
        /// Validates file against upload settings and storage quotas before saving. Automatically extracts
        /// metadata (dimensions, estimated print time) if the metadata extractor is available. Creates target
        /// folder if it doesn't exist.
        /// </remarks>
        Task<GcodeFileEntryDto> UploadFileAsync(string? path, IFormFile file, IGcodeUploadSettings uploadSettings, IGcodeUploadQuotaService quotaService, CancellationToken ct);

        /// <summary>
        /// Finalizes a chunked file upload after all chunks have been transferred.
        /// </summary>
        /// <param name="filePath">Full path to the assembled temporary file.</param>
        /// <param name="originalFileName">Original filename from the client (used if not already in metadata).</param>
        /// <param name="thumbnailPath">Path to an optional thumbnail image file generated during upload.</param>
        /// <param name="virtualDirectory">Virtual directory where file will be stored.</param>
        /// <param name="chunkedUploadService">Service managing chunked upload state.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Persisted GcodeFile domain model with database ID and relationships.</returns>
        /// <remarks>
        /// Called after all chunks have been uploaded and validated. Moves the temporary file to final storage,
        /// handles thumbnail organization, creates database record, and cleans up temporary upload state.
        /// </remarks>
        Task<GcodeFile?> FinalizeChunkedUploadAsync(string filePath, string? originalFileName, string? thumbnailPath, string? virtualDirectory, IChunkedUploadService chunkedUploadService, CancellationToken ct);

        /// <summary>
        /// Creates a new virtual directory at the specified path.
        /// </summary>
        /// <param name="path">Parent virtual path where directory will be created (e.g., '/prints'). Null defaults to root.</param>
        /// <param name="name">Name of the new directory.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>DTO representing the created folder with metadata.</returns>
        /// <exception cref="InvalidOperationException">Thrown if directory already exists or path is invalid.</exception>
        /// <remarks>
        /// Creates only a virtual folder in the database. No physical directory is created on the filesystem.
        /// Folder names are normalized and validated for safety.
        /// </remarks>
        Task<GcodeFileEntryDto> MakeDirectoryAsync(string? path, string? name, CancellationToken ct);

        /// <summary>
        /// Deletes one or more G-code files by ID from the database and filesystem.
        /// </summary>
        /// <param name="fileIds">Collection of file IDs (GUIDs) to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if at least one file was deleted successfully; false if no files were found or deleted.</returns>
        /// <remarks>
        /// Deletes files by their unique ID rather than path resolution. This allows deletion of orphaned files
        /// (database records with missing physical files). Database records are deleted first, then physical files.
        /// Missing physical files are counted as successful deletions since the database record was cleaned up.
        /// </remarks>
        Task<bool> DeleteFilesAsync(IEnumerable<Guid> fileIds, CancellationToken ct);

        /// <summary>
        /// Downloads a G-code file by its virtual path.
        /// </summary>
        /// <param name="path">Virtual path to the file to download (e.g., '/folder/file.gcode').</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Tuple of file bytes and suggested filename, or null if file not found.</returns>
        /// <remarks>
        /// Returns the complete file contents suitable for HTTP response transmission. Filename is suitable
        /// for Content-Disposition header.
        /// </remarks>
        Task<(byte[] Bytes, string FileName)?> DownloadAsync(string path, CancellationToken ct);

        /// <summary>
        /// Gets the file path for a G-code file by its ID.
        /// </summary>
        /// <param name="id">Unique identifier of the file.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>File path if found, otherwise null.</returns>
        Task<string?> GetFilePathAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Gets both the file path and original filename for a G-code file by its ID.
        /// Useful for downloads where we need the original filename in Content-Disposition header.
        /// </summary>
        /// <param name="id">G-code file ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Tuple of (filePath, originalFileName) if found, otherwise null.</returns>
        Task<(string FilePath, string OriginalFileName)?> GetFilePathAndNameAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Gets the thumbnail path for a G-code file by its ID.
        /// </summary>
        /// <param name="id">Unique identifier of the file.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Thumbnail path if found and exists, otherwise null.</returns>
        Task<string?> GetThumbnailPathAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Moves a G-code file or directory from one virtual path to another.
        /// </summary>
        /// <param name="sourcePath">Current virtual path.</param>
        /// <param name="destinationPath">New virtual path.</param>
        /// <param name="overwrite">If true, overwrites existing file at destination. If false, fails if destination exists.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Tuple of success flag, actual virtual path used, and whether the item is a directory.</returns>
        /// <remarks>
        /// Updates database references without moving physical files (virtual filesystem). Source and destination
        /// must be on the same virtual filesystem.
        /// </remarks>
        Task<(bool Ok, string VirtualPath, bool IsDirectory)> MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct);

        /// <summary>
        /// Moves a G-code file to a target folder by file ID and target folder path.
        /// </summary>
        /// <param name="fileId">Database ID of the file to move.</param>
        /// <param name="targetFolderPath">Virtual path of the destination folder.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if move succeeded, false if file or folder not found.</returns>
        /// <remarks>
        /// Provides an alternative to MoveAsync for operations that know the file by ID rather than path.
        /// Useful for API operations where the file is already selected by its ID.
        /// </remarks>
        Task<bool> MoveToFolderAsync(Guid fileId, string targetFolderPath, CancellationToken ct);

        /// <summary>
        /// Retrieves current upload settings and quota status for a user.
        /// </summary>
        /// <param name="userId">User ID to retrieve settings for.</param>
        /// <param name="uploadSettings">Global upload configuration.</param>
        /// <param name="quotaService">Service providing quota calculations.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>DTO containing upload settings, limits, and current quota usage.</returns>
        /// <remarks>
        /// Used by clients to display upload constraints and remaining quota before attempting uploads.
        /// </remarks>
        Task<GcodeUploadSettingsResponse> GetSettingsAsync(string userId, IGcodeUploadSettings uploadSettings, IGcodeUploadQuotaService quotaService, CancellationToken ct);

        #endregion

        #region Library Operations

        /// <summary>
        /// Queries the G-code library with optional filters for search, material, nozzle diameter, and printer model.
        /// </summary>
        /// <param name="search">Optional search term to match against filenames (case-insensitive partial match).</param>
        /// <param name="material">Optional material filter (e.g., 'PLA', 'PETG') to match RequiredMaterial field.</param>
        /// <param name="nozzleDiameter">Optional nozzle diameter in millimeters (e.g., 0.4, 0.6) to match RequiredNozzleDiameter field.</param>
        /// <param name="printerModelId">Optional printer model ID filter.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Read-only list of G-code file DTOs matching all specified criteria.</returns>
        /// <remarks>
        /// All filters are optional and combined with AND logic (all must match). Returns empty list if no matches found.
        /// This is the primary method for discovering files based on metadata attributes.
        /// </remarks>
        Task<IReadOnlyList<GcodeFileDto>> QueryLibraryAsync(string? search, string? material, double? nozzleDiameter, Guid? printerModelId, CancellationToken ct);

        /// <summary>
        /// Retrieves a specific G-code file by ID with full metadata and relationships.
        /// </summary>
        /// <param name="id">Unique identifier of the G-code file.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>DTO containing complete file metadata, or null if file not found.</returns>
        /// <remarks>
        /// Includes related data such as source printer information, target printer, and associated 3D model.
        /// Use this to display file details in the UI.
        /// </remarks>
        Task<GcodeFileDto?> GetFileAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Uploads a G-code file to the library with full metadata.
        /// </summary>
        /// <param name="file">The uploaded file from an HTTP request.</param>
        /// <param name="metadata">Metadata including description, tags, nozzle diameter, material, etc.</param>
        /// <param name="webRootPath">Application web root path for thumbnail URL generation.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>DTO representing the newly uploaded file with generated metadata.</returns>
        /// <exception cref="InvalidOperationException">Thrown if file already exists (duplicate hash detected) or is invalid.</exception>
        /// <remarks>
        /// Computes SHA256 hash to detect duplicates. Validates file format and size. Stores metadata in database
        /// for library queries. Separate from the file browser upload (UploadFileAsync) in that it emphasizes
        /// metadata capture over virtual folder organization.
        /// </remarks>
        Task<GcodeFileDto> UploadFileAsync(IFormFile file, CreateGcodeFileDto metadata, string webRootPath, CancellationToken ct);

        /// <summary>
        /// Updates metadata for an existing G-code file.
        /// </summary>
        /// <param name="id">Unique identifier of the file to update.</param>
        /// <param name="request">DTO containing metadata fields to update (null fields are skipped).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>DTO containing the updated file with all current metadata.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if file with specified ID not found.</exception>
        /// <remarks>
        /// Performs a partial update - only provided fields are modified. Does not update file content or core
        /// properties like upload time. Updates the UpdatedAt timestamp automatically.
        /// </remarks>
        Task<GcodeFileDto> UpdateFileAsync(Guid id, UpdateGcodeFileDto request, CancellationToken ct);

        /// <summary>
        /// Deletes a G-code file from the library.
        /// </summary>
        /// <param name="id">Unique identifier of the file to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if deletion succeeded, false if file not found or cannot be deleted (e.g., in use by active job).</returns>
        /// <remarks>
        /// Checks if the file is referenced by any active print queue jobs before allowing deletion. Removes both
        /// the physical file and its thumbnail from disk, and removes the database record. Handles file deletion
        /// errors gracefully by logging warnings without throwing exceptions.
        /// </remarks>
        Task<bool> DeleteFileAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Downloads a G-code file by ID, returning its complete contents.
        /// </summary>
        /// <param name="id">Unique identifier of the file to download.</param>
        /// <param name="webRootPath">Application web root path (used for path resolution).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Complete file contents as byte array, or null if file not found or cannot be read.</returns>
        /// <remarks>
        /// Returns the raw file contents suitable for HTTP response transmission. Checks both database record
        /// and filesystem existence before returning. Returns null if file metadata exists but physical file
        /// has been deleted.
        /// </remarks>
        Task<byte[]?> DownloadFileAsync(Guid id, string webRootPath, CancellationToken ct);

        #endregion
    }
}
