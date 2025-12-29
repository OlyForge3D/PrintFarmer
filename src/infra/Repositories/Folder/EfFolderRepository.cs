using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Folder;

/// <summary>
/// Entity Framework implementation of IFolderRepository
/// Uses IDbContextFactory for better testability and multi-operation scenarios
/// </summary>
public class EfFolderRepository : IFolderRepository
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public EfFolderRepository(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    /// <summary>
    /// Get an existing folder or create it if it doesn't exist.
    /// </summary>
    public async Task<Farm.Infrastructure.Domain.Folder> GetOrCreateFolderAsync(string directoryPath, string folderType, CancellationToken cancellationToken = default)
    {
        // Normalize path
        string normalizedPath = string.IsNullOrWhiteSpace(directoryPath) ? "/" : directoryPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, '/');

        using var context = _dbContextFactory.CreateDbContext();

        // Try to find existing folder
        var existingFolder = await context.Folders
            .FirstOrDefaultAsync(f => f.Path == normalizedPath && f.FolderType == folderType && !f.DeletedAt.HasValue, cancellationToken);

        if (existingFolder != null)
        {
            return existingFolder;
        }

        // Create new folder
        var newFolder = new Farm.Infrastructure.Domain.Folder
        {
            Id = Guid.NewGuid(),
            Path = normalizedPath,
            FolderType = folderType,
            CreatedAt = DateTime.UtcNow
        };

        context.Add(newFolder);
        await context.SaveChangesAsync(cancellationToken);

        return newFolder;
    }

    /// <summary>
    /// Get a folder by path and type without creating it
    /// </summary>
    public async Task<Farm.Infrastructure.Domain.Folder?> GetByPathAndTypeAsync(string path, string folderType, CancellationToken cancellationToken = default)
    {
        string normalizedPath = string.IsNullOrWhiteSpace(path) ? "/" : path.TrimEnd(System.IO.Path.DirectorySeparatorChar, '/');

        using var context = _dbContextFactory.CreateDbContext();

        return await context.Folders
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Path == normalizedPath && f.FolderType == folderType && !f.DeletedAt.HasValue, cancellationToken);
    }

    /// <summary>
    /// Save all pending changes to the database
    /// </summary>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        using var context = _dbContextFactory.CreateDbContext();
        await context.SaveChangesAsync(cancellationToken);
    }
}
