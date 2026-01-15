using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Services.FileManagement;

/// <summary>
/// Implementation of unified stored file operations.
/// Consolidates common file and thumbnail path management between GCode and 3D Models.
/// </summary>
public class StoredFileOperationsService : IStoredFileOperationsService
{
    private readonly IFileManagementService _fileManagementService;

    public StoredFileOperationsService(IFileManagementService fileManagementService)
    {
        _fileManagementService = fileManagementService ?? throw new ArgumentNullException(nameof(fileManagementService));
    }

    /// <summary>
    /// Builds the complete file path from a StoredFile entity.
    /// Combines FilePath + FileName to get the full disk path.
    /// </summary>
    public string GetFullFilePath(StoredFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Path.Combine(file.FilePath, file.FileName);
    }

    /// <summary>
    /// Gets just the filename from a full path (last component).
    /// </summary>
    public string GetFileNameFromPath(string fullPath)
    {
        return Path.GetFileName(fullPath);
    }

    /// <summary>
    /// Builds the complete thumbnail path from a StoredFile entity.
    /// Returns null if no thumbnail exists.
    /// Combines FilePath + ThumbnailFileName.
    /// </summary>
    public string? GetFullThumbnailPath(StoredFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.IsNullOrEmpty(file.ThumbnailFileName))
        {
            return null;
        }

        return Path.Combine(file.FilePath, file.ThumbnailFileName);
    }

    /// <summary>
    /// Generates a thumbnail filename with the specified extension.
    /// Uses the format: {fileId}_thumb{extension}
    /// </summary>
    public string GenerateThumbnailFileName(Guid fileId, string thumbnailExtension)
    {
        if (string.IsNullOrEmpty(thumbnailExtension))
        {
            throw new ArgumentException("Thumbnail extension cannot be null or empty", nameof(thumbnailExtension));
        }

        return $"{fileId}_thumb{thumbnailExtension}";
    }

    /// <summary>
    /// Extracts just the filename portion from a full path for storage in database.
    /// GcodeFile and Model3D store only the filename, not the full path.
    /// </summary>
    public string ExtractFileNameForStorage(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            throw new ArgumentException("Full path cannot be null or empty", nameof(fullPath));
        }

        return Path.GetFileName(fullPath);
    }

    /// <summary>
    /// Validates that a file path is within the expected storage directory.
    /// Prevents directory traversal attacks.
    /// </summary>
    public bool IsValidStoragePath(string candidatePath, string storageRoot)
    {
        return _fileManagementService.IsSafePath(candidatePath, storageRoot);
    }

    /// <summary>
    /// Builds the file download/view URL for a GCode file.
    /// Single source of truth for GCode file URLs.
    /// </summary>
    public string BuildGcodeFileUrl(Guid gcodeFileId)
    {
        return $"/api/gcode/file/{gcodeFileId}";
    }

    /// <summary>
    /// Builds the file download/view URL for a 3D model.
    /// Handles format-specific parameters (e.g., forceStl=true for 3MF files).
    /// Single source of truth for Model3D file URLs.
    /// </summary>
    public string BuildModel3DFileUrl(Guid modelId, ModelFileFormat format)
    {
        if (format == ModelFileFormat.TMF) // 3MF format needs conversion
        {
            return $"/api/3d-models/file/{modelId}?forceStl=true";
        }

        return $"/api/3d-models/file/{modelId}";
    }

    /// <summary>
    /// Builds the slicer job GCode download URL.
    /// Single source of truth for slicer job URLs.
    /// </summary>
    public string BuildSlicerJobGcodeUrl(Guid jobId)
    {
        return $"/api/slicer/jobs/{jobId}/gcode";
    }

    /// <summary>
    /// Builds the thumbnail URL for a GCode file.
    /// Single source of truth for GCode thumbnail URLs.
    /// </summary>
    public string BuildGcodeThumbnailUrl(Guid gcodeFileId)
    {
        return $"/api/gcode/thumbnail/{gcodeFileId}";
    }

    /// <summary>
    /// Builds the thumbnail URL for a 3D model.
    /// Single source of truth for Model3D thumbnail URLs.
    /// </summary>
    public string BuildModel3DThumbnailUrl(Guid modelId)
    {
        return $"/api/3d-models/thumbnail/{modelId}";
    }

    /// <summary>
    /// Resolves a relative or virtual path to an absolute path within a storage root.
    /// Handles virtual paths (leading slashes), relative paths, and already-absolute paths.
    /// This is the canonical path resolution logic used by all file controllers.
    /// </summary>
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
    public bool FileExistsAndIsSafe(string fullPath, string storageRoot)
    {
        if (string.IsNullOrEmpty(fullPath) || !_fileManagementService.IsSafePath(fullPath, storageRoot))
        {
            return false;
        }

        return File.Exists(fullPath);
    }

    /// <summary>
    /// Gets the appropriate content type for a file based on its extension.
    /// Provides unified content-type handling across GCode and Model3D downloads.
    /// All files default to application/octet-stream to force browser download behavior.
    /// </summary>
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
