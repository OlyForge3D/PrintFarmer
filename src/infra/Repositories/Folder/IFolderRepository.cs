using System.Collections.Generic;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Folder;

/// <summary>
/// Repository for folder persistence and queries
/// </summary>
public interface IFolderRepository
{
    /// <summary>
    /// Get or create a folder by path and type
    /// </summary>
    /// <param name="directoryPath">The directory path of the folder.</param>
    /// <param name="folderType">The type of folder (e.g., "gcode", "models").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The existing or newly created folder node.</returns>
    Task<Farm.Infrastructure.Domain.FolderNode> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Save all pending changes to the database
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a folder by path and type
    /// </summary>
    /// <param name="path">The folder path to search for.</param>
    /// <param name="folderType">The type of folder (e.g., "gcode", "models").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The folder node if found; otherwise, null.</returns>
    Task<Farm.Infrastructure.Domain.FolderNode?> GetByPathAndTypeAsync(string path, string folderType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all folders of a specific type
    /// </summary>
    /// <param name="folderType">The type of folder to retrieve (e.g., "gcode", "models")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of all folders of the specified type</returns>
    Task<List<FolderNode>> GetAllByFolderTypeAsync(string folderType, CancellationToken cancellationToken = default);
}
