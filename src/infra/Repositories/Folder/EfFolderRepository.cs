using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Folder;

/// <summary>
/// Entity Framework implementation of IFolderRepository
/// </summary>
public class EfFolderRepository(AppDbContext db) : IFolderRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// Get an existing folder or create it if it doesn't exist.
    /// </summary>
    /// <param name="directoryPath">The virtual directory path for the folder.</param>
    /// <param name="folderType">The type of folder (e.g., "models", "gcode").</param>
    /// <param name="cancellationToken">Cancellation token for async operation.</param>
    /// <returns>The existing or newly created folder entity.</returns>
    public async Task<Farm.Infrastructure.Domain.FolderNode> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken cancellationToken = default)
    {
        // Normalize path: ensure root is "/" not empty string
        string normalizedPath = string.IsNullOrWhiteSpace(directoryPath) ? "/" : directoryPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, '/');

        // If trimming left us with empty string, it was root "/"
        if (string.IsNullOrEmpty(normalizedPath))
        {
            normalizedPath = "/";
        }

        // Try to find existing folder
        FolderNode? existingFolder = await _db.Set<FolderNode>()
            .FirstOrDefaultAsync(f => f.Path == normalizedPath && f.FolderType == folderType && !f.DeletedAt.HasValue, cancellationToken);

        if (existingFolder != null)
        {
            return existingFolder;
        }

        // Create new folder
        var newFolder = new Farm.Infrastructure.Domain.FolderNode
        {
            Id = Guid.NewGuid(),
            Path = normalizedPath,
            FolderType = folderType,
            CreatedAt = DateTime.UtcNow
        };

        _db.Add(newFolder);
        await _db.SaveChangesAsync(cancellationToken);

        return newFolder;
    }

    /// <summary>
    /// Get a folder by path and type without creating it
    /// </summary>
    /// <param name="path">The virtual path of the folder to retrieve.</param>
    /// <param name="folderType">The type of folder (e.g., "models", "gcode").</param>
    /// <param name="cancellationToken">Cancellation token for async operation.</param>
    /// <returns>The folder entity if found; otherwise, null.</returns>
    public async Task<Farm.Infrastructure.Domain.FolderNode?> GetByPathAndTypeAsync(string path, string folderType, CancellationToken cancellationToken = default)
    {
        string normalizedPath = string.IsNullOrWhiteSpace(path) ? "/" : path.TrimEnd(System.IO.Path.DirectorySeparatorChar, '/');

        return await _db.Set<FolderNode>()
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Path == normalizedPath && f.FolderType == folderType && !f.DeletedAt.HasValue, cancellationToken);
    }

    /// <summary>
    /// Get all folders of a specific type
    /// </summary>
    /// <param name="folderType">The type of folders to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token for async operation.</param>
    /// <returns>List of all folders matching the specified type.</returns>
    public async Task<List<FolderNode>> GetAllByFolderTypeAsync(string folderType, CancellationToken cancellationToken = default)
    {
        return await _db.Set<FolderNode>()
            .AsNoTracking()
            .Where(f => f.FolderType == folderType && !f.DeletedAt.HasValue)
            .OrderBy(f => f.Path)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Save all pending changes to the database
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for async operation.</param>
    /// <returns>A task representing the asynchronous save operation.</returns>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _db.SaveChangesAsync(cancellationToken);
    }
}
