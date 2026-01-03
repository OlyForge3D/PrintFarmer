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
    /// Builds a download-based thumbnail URL using query parameters (path-based pattern).
    /// Returns null if file has no thumbnail.
    /// Format: /api/{endpoint}/download?path={relativePath}
    /// </summary>
    public string? BuildThumbnailUrl(StoredFile file, string apiDownloadEndpoint, string storageDirectory)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.IsNullOrEmpty(file.ThumbnailFileName))
        {
            return null;
        }

        if (string.IsNullOrEmpty(apiDownloadEndpoint))
        {
            throw new ArgumentException("API download endpoint cannot be null or empty", nameof(apiDownloadEndpoint));
        }

        if (string.IsNullOrEmpty(storageDirectory))
        {
            throw new ArgumentException("Storage directory cannot be null or empty", nameof(storageDirectory));
        }

        string fullThumbnailPath = Path.Combine(file.FilePath, file.ThumbnailFileName);
        string normalizedStorageDir = Path.GetFullPath(storageDirectory);
        string normalizedThumbnailPath = Path.GetFullPath(fullThumbnailPath);

        // Compute relative path for URL
        if (normalizedThumbnailPath.StartsWith(normalizedStorageDir, StringComparison.Ordinal))
        {
            string relativePath = normalizedThumbnailPath.Substring(normalizedStorageDir.Length)
                .TrimStart(Path.DirectorySeparatorChar, '/');
            relativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            return $"{apiDownloadEndpoint}?path={Uri.EscapeDataString(relativePath)}";
        }

        // Fallback to using just the filename if not under storage directory
        return $"{apiDownloadEndpoint}?path={Uri.EscapeDataString(file.ThumbnailFileName)}";
    }

    /// <summary>
    /// Validates that a file path is within the expected storage directory.
    /// Prevents directory traversal attacks.
    /// </summary>
    public bool IsValidStoragePath(string candidatePath, string storageRoot)
    {
        return _fileManagementService.IsSafePath(candidatePath, storageRoot);
    }
}
