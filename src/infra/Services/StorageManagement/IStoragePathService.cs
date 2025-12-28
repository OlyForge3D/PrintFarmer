namespace Farm.Infrastructure.Services.StorageManagement;

/// <summary>
/// Service for managing file storage paths across different deployment architectures.
/// Supports both Docker and Kubernetes deployments with shared volume configurations.
/// </summary>
public interface IStoragePathService
{
    /// <summary>
    /// Get the base directory for gcode files storage.
    /// In Docker/K8s: Uses mounted external volume path
    /// In local dev: Uses wwwroot relative path
    /// </summary>
    string GetGcodeStorageDirectory();

    /// <summary>
    /// Get the directory for gcode thumbnails.
    /// </summary>
    string GetThumbnailDirectory();

    /// <summary>
    /// Get the directory for uploaded model files.
    /// </summary>
    string GetModelUploadDirectory();

    /// <summary>
    /// Get the directory for slicer profiles.
    /// </summary>
    string GetSlicerProfilesDirectory();

    /// <summary>
    /// Ensure all storage directories exist.
    /// </summary>
    Task EnsureDirectoriesExistAsync();
}
