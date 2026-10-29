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
    /// Get the directory for camera snapshots captured on print events.
    /// </summary>
    string GetSnapshotStorageDirectory();

    /// <summary>
    /// Get the dedicated root for printer-file catalog thumbnails. It must not overlap the
    /// G-code library or model upload roots, so library file APIs cannot read or write it.
    /// In multi-instance deployments, mount it on storage shared by every API instance.
    /// </summary>
    string GetPrinterFileThumbnailDirectory();

    /// <summary>
    /// Ensure all storage directories exist.
    /// </summary>
    Task EnsureDirectoriesExistAsync();
}
