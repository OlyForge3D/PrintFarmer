using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.FileManagement;

/// <summary>
/// Unified operations service for stored files (GCode and 3D Models).
/// Consolidates common file and thumbnail path management patterns.
/// </summary>
public interface IStoredFileOperationsService
{
    /// <summary>
    /// Builds the complete file path from a StoredFile entity.
    /// Combines FilePath + FileName to get the full disk path.
    /// </summary>
    string GetFullFilePath(StoredFile file);

    /// <summary>
    /// Gets just the filename from a full path (last component).
    /// </summary>
    string GetFileNameFromPath(string fullPath);

    /// <summary>
    /// Builds the complete thumbnail path from a StoredFile entity.
    /// Returns null if no thumbnail exists.
    /// Combines FilePath + ThumbnailFileName.
    /// </summary>
    string? GetFullThumbnailPath(StoredFile file);

    /// <summary>
    /// Generates a thumbnail filename with the specified extension.
    /// Uses the format: {fileId}_thumb{extension}
    /// </summary>
    string GenerateThumbnailFileName(Guid fileId, string thumbnailExtension);

    /// <summary>
    /// Extracts just the filename portion from a full path for storage in database.
    /// GcodeFile and Model3D store only the filename, not the full path.
    /// </summary>
    string ExtractFileNameForStorage(string fullPath);

    /// <summary>
    /// Builds a download-based thumbnail URL using query parameters (path-based pattern).
    /// Returns null if file has no thumbnail.
    /// Format: /api/{endpoint}/download?path={relativePath}
    /// </summary>
    /// <param name="file">The stored file containing thumbnail filename and path.</param>
    /// <param name="apiDownloadEndpoint">The API download endpoint (e.g., "/api/gcode-files/download" or "/api/3d-models/download").</param>
    /// <param name="storageDirectory">The root storage directory for computing relative paths.</param>
    /// <remarks>
    /// This method computes the relative path from the storage directory to the thumbnail file,
    /// properly handling path normalization and URL encoding for safe transmission. This pattern
    /// is more efficient than id-based lookups because the thumbnail path is already known from
    /// the file metadata, avoiding unnecessary database queries.
    /// </remarks>
    string? BuildThumbnailUrl(StoredFile file, string apiDownloadEndpoint, string storageDirectory);

    /// <summary>
    /// Validates that a file path is within the expected storage directory.
    /// Prevents directory traversal attacks.
    /// </summary>
    bool IsValidStoragePath(string candidatePath, string storageRoot);
}
