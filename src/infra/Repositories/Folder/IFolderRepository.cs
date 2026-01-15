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
    Task<Farm.Infrastructure.Domain.FolderNode> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Save all pending changes to the database
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a folder by path and type
    /// </summary>
    Task<Farm.Infrastructure.Domain.FolderNode?> GetByPathAndTypeAsync(string path, string folderType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all folders of a specific type
    /// </summary>
    /// <param name="folderType">The type of folder to retrieve (e.g., "gcode", "models")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of all folders of the specified type</returns>
    Task<List<FolderNode>> GetAllByFolderTypeAsync(string folderType, CancellationToken cancellationToken = default);
}
