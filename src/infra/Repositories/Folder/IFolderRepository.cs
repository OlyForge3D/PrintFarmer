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
    Task<Farm.Infrastructure.Domain.Folder> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Save all pending changes to the database
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a folder by path and type
    /// </summary>
    Task<Farm.Infrastructure.Domain.Folder?> GetByPathAndTypeAsync(string path, string folderType, CancellationToken cancellationToken = default);
}
