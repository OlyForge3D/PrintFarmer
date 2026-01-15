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
    /// Validates that a file path is within the expected storage directory.
    /// Prevents directory traversal attacks.
    /// </summary>
    /// <param name="candidatePath">The file path to validate.</param>
    /// <param name="storageRoot">The root storage directory.</param>
    bool IsValidStoragePath(string candidatePath, string storageRoot);

    /// <summary>
    /// Builds the file download/view URL for a GCode file.
    /// Format: /api/gcode/file/{id}
    /// </summary>
    string BuildGcodeFileUrl(Guid gcodeFileId);

    /// <summary>
    /// Builds the thumbnail URL for a GCode file.
    /// Format: /api/gcode/thumbnail/{id}
    /// </summary>
    string BuildGcodeThumbnailUrl(Guid gcodeFileId);

    /// <summary>
    /// Builds the file download/view URL for a 3D model.
    /// Handles format-specific parameters (e.g., forceStl=true for 3MF files).
    /// Format: /api/3d-models/file/{id}[?forceStl=true]
    /// </summary>
    string BuildModel3DFileUrl(Guid modelId, ModelFileFormat format);

    /// <summary>
    /// Builds the thumbnail URL for a 3D model.
    /// Format: /api/3d-models/thumbnail/{id}
    /// </summary>
    string BuildModel3DThumbnailUrl(Guid modelId);

    /// <summary>
    /// Builds the slicer job GCode download URL.
    /// Format: /api/slicer/jobs/{jobId}/gcode
    /// </summary>
    string BuildSlicerJobGcodeUrl(Guid jobId);

    /// <summary>
    /// Resolves a relative or virtual path to an absolute path within a storage root.
    /// Handles virtual paths (leading slashes), relative paths, and already-absolute paths.
    /// This is the canonical path resolution logic used by all file controllers.
    /// </summary>
    /// <param name="relativePath">The relative or virtual path to resolve.</param>
    /// <param name="storageRoot">The root storage directory (must be absolute).</param>
    /// <returns>The resolved absolute path.</returns>
    string ResolveStoragePath(string? relativePath, string storageRoot);

    /// <summary>
    /// Validates that a file exists at the given path and is safe to serve.
    /// Performs both safety check (directory traversal prevention) and existence check.
    /// </summary>
    /// <param name="fullPath">The absolute file path to validate.</param>
    /// <param name="storageRoot">The root storage directory for safety validation.</param>
    /// <returns>True if file is safe and exists; false otherwise.</returns>
    bool FileExistsAndIsSafe(string fullPath, string storageRoot);

    /// <summary>
    /// Gets the appropriate content type for a file based on its extension.
    /// Used for consistent download content-type headers across all file types.
    /// </summary>
    /// <param name="fileExtension">The file extension (e.g., ".gcode", ".stl", ".obj").</param>
    /// <returns>The MIME type to use for the file.</returns>
    string GetContentTypeForFile(string fileExtension);
}
