using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.FileManagement;

/// <summary>
/// Implementation of unified stored file operations.
/// Consolidates common file and thumbnail path management between GCode and 3D Models.
/// </summary>
public class StoredFileOperationsService(IFileManagementService fileManagementService) : IStoredFileOperationsService
{
    private readonly IFileManagementService _fileManagementService = fileManagementService ?? throw new ArgumentNullException(nameof(fileManagementService));

    /// <summary>
    /// Builds the complete file path from a StoredFile entity.
    /// Combines FilePath + FileName to get the full disk path.
    /// </summary>
    /// <param name="file">The stored file entity containing path and filename.</param>
    public string GetFullFilePath(StoredFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Path.Combine(file.FilePath, file.FileName);
    }

    /// <summary>
    /// Gets just the filename from a full path (last component).
    /// </summary>
    /// <param name="fullPath">The full file path to extract the filename from.</param>
    public string GetFileNameFromPath(string fullPath)
    {
        return Path.GetFileName(fullPath);
    }

    /// <summary>
    /// Builds the complete thumbnail path from a StoredFile entity.
    /// Returns null if no thumbnail exists.
    /// Combines FilePath + ThumbnailFileName.
    /// </summary>
    /// <param name="file">The stored file entity containing path and thumbnail filename.</param>
    public string? GetFullThumbnailPath(StoredFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return string.IsNullOrEmpty(file.ThumbnailFileName) ? null : Path.Combine(file.FilePath, file.ThumbnailFileName);
    }

    /// <summary>
    /// Generates a thumbnail filename with the specified extension.
    /// Uses the format: {fileId}_thumb{extension}
    /// </summary>
    /// <param name="fileId">The unique identifier of the file.</param>
    /// <param name="thumbnailExtension">The file extension for the thumbnail (e.g., ".png").</param>
    public string GenerateThumbnailFileName(Guid fileId, string thumbnailExtension)
    {
        return string.IsNullOrEmpty(thumbnailExtension)
            ? throw new ArgumentException("Thumbnail extension cannot be null or empty", nameof(thumbnailExtension))
            : $"{fileId}_thumb{thumbnailExtension}";
    }

    /// <summary>
    /// Extracts just the filename portion from a full path for storage in database.
    /// GcodeFile and Model3D store only the filename, not the full path.
    /// </summary>
    /// <param name="fullPath">The full file path to extract the filename from.</param>
    public string ExtractFileNameForStorage(string fullPath)
    {
        return string.IsNullOrEmpty(fullPath)
            ? throw new ArgumentException("Full path cannot be null or empty", nameof(fullPath))
            : Path.GetFileName(fullPath);
    }

    /// <summary>
    /// Validates that a file path is within the expected storage directory.
    /// Prevents directory traversal attacks.
    /// </summary>
    /// <param name="candidatePath">The path to validate.</param>
    /// <param name="storageRoot">The allowed storage root directory.</param>
    public bool IsValidStoragePath(string candidatePath, string storageRoot)
    {
        return _fileManagementService.IsSafePath(candidatePath, storageRoot);
    }

    /// <summary>
    /// Builds the file download/view URL for a GCode file.
    /// Single source of truth for GCode file URLs.
    /// </summary>
    /// <param name="gcodeFileId">The unique identifier of the GCode file.</param>
    public string BuildGcodeFileUrl(Guid gcodeFileId)
    {
        return $"/api/gcode-files/file/{gcodeFileId}";
    }

    /// <summary>
    /// Builds the file download/view URL for a 3D model.
    /// Handles format-specific parameters (e.g., forceStl=true for 3MF files).
    /// Single source of truth for Model3D file URLs.
    /// </summary>
    /// <param name="modelId">The unique identifier of the 3D model.</param>
    /// <param name="format">The file format of the model.</param>
    public string BuildModel3DFileUrl(Guid modelId, ModelFileFormat format)
    {
        return format == ModelFileFormat.TMF ? $"/api/3d-models/file/{modelId}?forceStl=true" : $"/api/3d-models/file/{modelId}";
    }

    /// <summary>
    /// Builds the slicer job GCode download URL.
    /// Single source of truth for slicer job URLs.
    /// </summary>
    /// <param name="jobId">The unique identifier of the slicer job.</param>
    public string BuildSlicerJobGcodeUrl(Guid jobId)
    {
        return $"/api/slicer/jobs/{jobId}/gcode";
    }

    /// <summary>
    /// Builds the thumbnail URL for a GCode file.
    /// Single source of truth for GCode thumbnail URLs.
    /// </summary>
    /// <param name="gcodeFileId">The unique identifier of the GCode file.</param>
    public string BuildGcodeThumbnailUrl(Guid gcodeFileId)
    {
        return $"/api/gcode-files/thumbnail/{gcodeFileId}";
    }

    /// <summary>
    /// Builds the thumbnail URL for a 3D model.
    /// Single source of truth for Model3D thumbnail URLs.
    /// </summary>
    /// <param name="modelId">The unique identifier of the 3D model.</param>
    public string BuildModel3DThumbnailUrl(Guid modelId)
    {
        return $"/api/3d-models/thumbnail/{modelId}";
    }

    /// <summary>
    /// Resolves a relative or virtual path to an absolute path within a storage root.
    /// Handles virtual paths (leading slashes), relative paths, and already-absolute paths.
    /// This is the canonical path resolution logic used by all file controllers.
    /// </summary>
    /// <param name="relativePath">The relative or virtual path to resolve.</param>
    /// <param name="storageRoot">The storage root directory to resolve against.</param>
    public string ResolveStoragePath(string? relativePath, string storageRoot)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return string.Empty;
        }

        // Normalize virtual paths - strip leading slashes to handle virtual path format
        string normalizedPath = relativePath.TrimStart('/').Trim();
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return storageRoot;
        }

        // If the normalized path is already absolute, return as-is (Windows C:\ or Unix /mnt/etc)
        if (Path.IsPathRooted(normalizedPath))
        {
            return normalizedPath;
        }

        // Combine relative path with storage root (guaranteed to be absolute)
        return Path.Combine(storageRoot, normalizedPath);
    }

    /// <summary>
    /// Validates that a file exists at the given path and is safe to serve.
    /// Performs both safety check (directory traversal prevention) and existence check.
    /// </summary>
    /// <param name="fullPath">The full path to the file to check.</param>
    /// <param name="storageRoot">The allowed storage root directory.</param>
    public bool FileExistsAndIsSafe(string fullPath, string storageRoot)
    {
        return string.IsNullOrEmpty(fullPath) || !_fileManagementService.IsSafePath(fullPath, storageRoot) ? false : File.Exists(fullPath);
    }

    /// <summary>
    /// Gets the appropriate content type for a file based on its extension.
    /// Provides unified content-type handling across GCode and Model3D downloads.
    /// All files default to application/octet-stream to force browser download behavior.
    /// </summary>
    /// <param name="fileExtension">The file extension including the leading dot (e.g., ".png").</param>
    public string GetContentTypeForFile(string fileExtension)
    {
        return fileExtension.ToLowerInvariant() switch
        {
            // Common image thumbnail types
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",

            // GCode files - force download
            ".gcode" => "application/octet-stream",
            ".bgcode" => "application/octet-stream",

            // 3D Model formats
            ".stl" => "application/vnd.ms-pki.stl",
            ".3mf" => "model/3mf",
            ".obj" => "text/plain",
            ".ply" => "application/octet-stream",

            // Default to octet-stream for unknown types (forces download)
            _ => "application/octet-stream"
        };
    }
}
