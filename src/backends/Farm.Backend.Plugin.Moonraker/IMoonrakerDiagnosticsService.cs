using Farm.Infrastructure.Contracts.Printers.Moonraker;

namespace Farm.Backend.Plugin.Moonraker;

/// <summary>
/// Service for Moonraker diagnostics and file system exploration.
/// Provides access to file roots and directory listings for debugging and administration.
/// </summary>
public interface IMoonrakerDiagnosticsService
{
    /// <summary>
    /// Gets all available file roots (storage locations) from the Moonraker server.
    /// </summary>
    /// <param name="url">The base URL of the Moonraker server.</param>
    /// <returns>An array of file roots, or null if the operation fails.</returns>
    Task<FileRoot[]?> GetFileRootsAsync(string url);

    /// <summary>
    /// Gets directory information including files and subdirectories.
    /// </summary>
    /// <param name="url">The base URL of the Moonraker server.</param>
    /// <param name="path">The path to list (defaults to "gcodes").</param>
    /// <returns>Directory information, or null if the operation fails.</returns>
    Task<MoonrakerDirectoryInfo?> GetDirectoryAsync(string url, string path = "gcodes");

    /// <summary>
    /// Gets a detailed list of files including metadata like size and modification time.
    /// </summary>
    /// <param name="url">The base URL of the Moonraker server.</param>
    /// <param name="root">The root storage location (defaults to "gcodes").</param>
    /// <param name="path">Optional path within the root to list.</param>
    /// <returns>An array of file information, or null if the operation fails.</returns>
    Task<MoonrakerFileInfo[]?> GetDetailedFileListAsync(string url, string root = "gcodes", string? path = null);
}
