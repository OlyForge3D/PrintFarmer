using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Folder;

/// <summary>
/// Entity Framework implementation of IFolderRepository
/// </summary>
public class EfFolderRepository : IFolderRepository
{
    private readonly AppDbContext _db;

    public EfFolderRepository(AppDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Get an existing folder or create it if it doesn't exist.
    /// </summary>
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
        var existingFolder = await _db.Folders
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
    public async Task<Farm.Infrastructure.Domain.FolderNode?> GetByPathAndTypeAsync(string path, string folderType, CancellationToken cancellationToken = default)
    {
        string normalizedPath = string.IsNullOrWhiteSpace(path) ? "/" : path.TrimEnd(System.IO.Path.DirectorySeparatorChar, '/');

        return await _db.Folders
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Path == normalizedPath && f.FolderType == folderType && !f.DeletedAt.HasValue, cancellationToken);
    }

    /// <summary>
    /// Save all pending changes to the database
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _db.SaveChangesAsync(cancellationToken);
    }
}
