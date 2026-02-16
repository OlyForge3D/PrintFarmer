using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Tags;

public class EfTagRepository(AppDbContext dbContext) : ITagRepository
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<Tag?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await _dbContext.Set<Tag>()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    public async Task<Tag?> GetByNameAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // Since tags are normalized to PascalCase on creation, we can do exact matching
        return await _dbContext.Set<Tag>()
            .FirstOrDefaultAsync(t => t.Name == name, ct);
    }

    public async Task<IReadOnlyList<Tag>> ListAllAsync(CancellationToken ct)
    {
        return await _dbContext.Set<Tag>()
            .OrderBy(t => t.Name)
            .ToListAsync(ct);
    }

    public async Task AddAsync(Tag tag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tag);

        _ = await _dbContext.Set<Tag>().AddAsync(tag, ct);
    }

    public async Task RemoveAsync(Tag tag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tag);

        _ = _dbContext.Set<Tag>().Remove(tag);
        await Task.CompletedTask; // Repository pattern consistency
    }

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        _ = await _dbContext.SaveChangesAsync(ct);
    }

    // ============================================================================
    // OBJECT-AGNOSTIC METHODS (work with any object type)
    // ============================================================================

    /// <summary>
    /// Check if an object has a specific tag (object-agnostic).
    /// Searches both GcodeFile and Model3D collections.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object to check.</param>
    /// <param name="tagId">The unique identifier of the tag to look for.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<bool> HasTagAsync(Guid objectId, Guid tagId, CancellationToken ct)
    {
        bool hasInGcodeFile = await _dbContext.GcodeFiles
            .Where(g => g.Id == objectId)
            .AnyAsync(g => g.Tags.Any(t => t.Id == tagId), ct);

        if (hasInGcodeFile)
        {
            return true;
        }

        bool hasInModel3D = await _dbContext.Set<Model3D>()
            .Where(m => m.Id == objectId)
            .AnyAsync(m => m.Tags.Any(t => t.Id == tagId), ct);

        return hasInModel3D;
    }

    /// <summary>
    /// Assign a tag to an object (object-agnostic).
    /// Searches both GcodeFile and Model3D to find the object.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object to tag.</param>
    /// <param name="tagId">The unique identifier of the tag to assign.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task AssignTagAsync(Guid objectId, Guid tagId, CancellationToken ct)
    {
        Tag tag = await _dbContext.Set<Tag>().FirstOrDefaultAsync(t => t.Id == tagId, ct) ?? throw new InvalidOperationException($"Tag with ID {tagId} not found.");

        // Try GcodeFile first
        GcodeFile? gcodeFile = await _dbContext.GcodeFiles
            .Include(g => g.Tags)
            .FirstOrDefaultAsync(g => g.Id == objectId, ct);

        if (gcodeFile != null)
        {
            if (!gcodeFile.Tags.Any(t => t.Id == tagId))
            {
                gcodeFile.Tags.Add(tag);
            }

            return;
        }

        // Try Model3D
        Model3D? model3d = await _dbContext.Set<Model3D>()
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.Id == objectId, ct);

        if (model3d != null)
        {
            if (!model3d.Tags.Any(t => t.Id == tagId))
            {
                model3d.Tags.Add(tag);
            }
        }
    }

    /// <summary>
    /// Remove a tag from an object (object-agnostic).
    /// Searches both GcodeFile and Model3D to find the object.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object to remove the tag from.</param>
    /// <param name="tagId">The unique identifier of the tag to remove.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task RemoveTagAsync(Guid objectId, Guid tagId, CancellationToken ct)
    {
        Tag? tag = await _dbContext.Set<Tag>().FirstOrDefaultAsync(t => t.Id == tagId, ct);
        if (tag == null)
        {
            return;
        }

        // Try GcodeFile first
        GcodeFile? gcodeFile = await _dbContext.GcodeFiles
            .Include(g => g.Tags)
            .FirstOrDefaultAsync(g => g.Id == objectId, ct);

        if (gcodeFile != null)
        {
            gcodeFile.Tags.Remove(tag);
            return;
        }

        // Try Model3D
        Model3D? model3d = await _dbContext.Set<Model3D>()
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.Id == objectId, ct);

        if (model3d != null)
        {
            model3d.Tags.Remove(tag);
        }
    }

    /// <summary>
    /// Get all tags for an object (object-agnostic).
    /// Searches both GcodeFile and Model3D to find the object.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object to retrieve tags for.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<IReadOnlyList<Tag>> GetTagsByObjectAsync(Guid objectId, CancellationToken ct)
    {
        List<Tag> gcodeFileTags = await _dbContext.GcodeFiles
            .Where(g => g.Id == objectId)
            .SelectMany(g => g.Tags)
            .ToListAsync(ct);

        if (gcodeFileTags.Count > 0)
        {
            return gcodeFileTags;
        }

        List<Tag> model3dTags = await _dbContext.Set<Model3D>()
            .Where(m => m.Id == objectId)
            .SelectMany(m => m.Tags)
            .ToListAsync(ct);

        return model3dTags;
    }

    /// <summary>
    /// Remove all tags from an object (object-agnostic).
    /// Searches both GcodeFile and Model3D to find the object.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object to remove all tags from.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task RemoveAllTagsFromObjectAsync(Guid objectId, CancellationToken ct)
    {
        // Try GcodeFile first
        GcodeFile? gcodeFile = await _dbContext.GcodeFiles
            .Include(g => g.Tags)
            .FirstOrDefaultAsync(g => g.Id == objectId, ct);

        if (gcodeFile != null)
        {
            gcodeFile.Tags.Clear();
            return;
        }

        // Try Model3D
        Model3D? model3d = await _dbContext.Set<Model3D>()
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.Id == objectId, ct);

        if (model3d != null)
        {
            model3d.Tags.Clear();
        }
    }

    // ============================================================================
    // TYPE-FILTERED METHODS (optional, for when you need objects of specific type)
    // ============================================================================

    /// <summary>
    /// Get all objects of a specific type that have a specific tag using skip-navigation.
    /// </summary>
    /// <param name="tagId">The unique identifier of the tag to filter by.</param>
    /// <param name="objectType">The type of object to search (e.g., "GcodeFile" or "Model3D").</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<IReadOnlyList<Guid>> GetObjectsByTagAsync(Guid tagId, string objectType, CancellationToken ct)
    {
        return objectType switch
        {
            "GcodeFile" => await _dbContext.GcodeFiles
                .Where(g => g.Tags.Any(t => t.Id == tagId))
                .Select(g => g.Id)
                .ToListAsync(ct),
            "Model3D" => await _dbContext.Set<Model3D>()
                .Where(m => m.Tags.Any(t => t.Id == tagId))
                .Select(m => m.Id)
                .ToListAsync(ct),
            _ => []
        };
    }

    /// <summary>
    /// Get objects that have ALL of the specified tags (intersection).
    /// </summary>
    /// <param name="tagIds">The collection of tag identifiers that objects must have all of.</param>
    /// <param name="objectType">The type of object to search (e.g., "GcodeFile" or "Model3D").</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<IReadOnlyList<Guid>> GetObjectsWithAllTagsAsync(IEnumerable<Guid> tagIds, string objectType, CancellationToken ct)
    {
        var tagIdList = tagIds.ToList();
        return tagIdList.Count == 0
            ? []
            : objectType switch
            {
                "GcodeFile" => await _dbContext.GcodeFiles
                    .Where(g => tagIdList.All(tagId => g.Tags.Any(t => t.Id == tagId)))
                    .Select(g => g.Id)
                    .ToListAsync(ct),
                "Model3D" => await _dbContext.Set<Model3D>()
                    .Where(m => tagIdList.All(tagId => m.Tags.Any(t => t.Id == tagId)))
                    .Select(m => m.Id)
                    .ToListAsync(ct),
                _ => []
            };
    }

    /// <summary>
    /// Get objects that have ANY of the specified tags (union).
    /// </summary>
    /// <param name="tagIds">The collection of tag identifiers to match against.</param>
    /// <param name="objectType">The type of object to search (e.g., "GcodeFile" or "Model3D").</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<IReadOnlyList<Guid>> GetObjectsWithAnyTagsAsync(IEnumerable<Guid> tagIds, string objectType, CancellationToken ct)
    {
        var tagIdList = tagIds.ToList();
        return tagIdList.Count == 0
            ? []
            : objectType switch
            {
                "GcodeFile" => await _dbContext.GcodeFiles
                    .Where(g => g.Tags.Any(t => tagIdList.Contains(t.Id)))
                    .Select(g => g.Id)
                    .ToListAsync(ct),
                "Model3D" => await _dbContext.Set<Model3D>()
                    .Where(m => m.Tags.Any(t => tagIdList.Contains(t.Id)))
                    .Select(m => m.Id)
                    .ToListAsync(ct),
                _ => []
            };
    }

    /// <summary>
    /// Check if an object has a specific tag using skip-navigation.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object to check.</param>
    /// <param name="tagId">The unique identifier of the tag to look for.</param>
    /// <param name="objectType">The type of object to search (e.g., "GcodeFile" or "Model3D").</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<bool> HasTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct)
    {
        return objectType switch
        {
            "GcodeFile" => await _dbContext.GcodeFiles
                .Where(g => g.Id == objectId)
                .AnyAsync(g => g.Tags.Any(t => t.Id == tagId), ct),
            "Model3D" => await _dbContext.Set<Model3D>()
                .Where(m => m.Id == objectId)
                .AnyAsync(m => m.Tags.Any(t => t.Id == tagId), ct),
            _ => false
        };
    }

    /// <summary>
    /// Assign a tag to an object by adding it to the skip-navigation collection.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object to tag.</param>
    /// <param name="tagId">The unique identifier of the tag to assign.</param>
    /// <param name="objectType">The type of object to tag (e.g., "GcodeFile" or "Model3D").</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task AssignTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct)
    {
        Tag tag = await _dbContext.Set<Tag>().FirstOrDefaultAsync(t => t.Id == tagId, ct) ?? throw new InvalidOperationException($"Tag with ID {tagId} not found.");

        switch (objectType)
        {
            case "GcodeFile":
                GcodeFile? gcodeFile = await _dbContext.GcodeFiles
                    .Include(g => g.Tags)
                    .FirstOrDefaultAsync(g => g.Id == objectId, ct);
                if (gcodeFile != null && !gcodeFile.Tags.Any(t => t.Id == tagId))
                {
                    gcodeFile.Tags.Add(tag);
                }

                break;

            case "Model3D":
                Model3D? model3d = await _dbContext.Set<Model3D>()
                    .Include(m => m.Tags)
                    .FirstOrDefaultAsync(m => m.Id == objectId, ct);
                if (model3d != null && !model3d.Tags.Any(t => t.Id == tagId))
                {
                    model3d.Tags.Add(tag);
                }

                break;
        }
    }

    /// <summary>
    /// Remove a tag from an object using skip-navigation.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object to remove the tag from.</param>
    /// <param name="tagId">The unique identifier of the tag to remove.</param>
    /// <param name="objectType">The type of object (e.g., "GcodeFile" or "Model3D").</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task RemoveTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct)
    {
        Tag? tag = await _dbContext.Set<Tag>().FirstOrDefaultAsync(t => t.Id == tagId, ct);
        if (tag == null)
        {
            return;
        }

        switch (objectType)
        {
            case "GcodeFile":
                GcodeFile? gcodeFile = await _dbContext.GcodeFiles
                    .Include(g => g.Tags)
                    .FirstOrDefaultAsync(g => g.Id == objectId, ct);
                if (gcodeFile != null)
                {
                    gcodeFile.Tags.Remove(tag);
                }

                break;

            case "Model3D":
                Model3D? model3d = await _dbContext.Set<Model3D>()
                    .Include(m => m.Tags)
                    .FirstOrDefaultAsync(m => m.Id == objectId, ct);
                if (model3d != null)
                {
                    model3d.Tags.Remove(tag);
                }

                break;
        }
    }

    /// <summary>
    /// Remove all tags from a specific object using skip-navigation.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object to remove all tags from.</param>
    /// <param name="objectType">The type of object (e.g., "GcodeFile" or "Model3D").</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task RemoveAllTagsFromObjectAsync(Guid objectId, string objectType, CancellationToken ct)
    {
        switch (objectType)
        {
            case "GcodeFile":
                GcodeFile? gcodeFile = await _dbContext.GcodeFiles
                    .Include(g => g.Tags)
                    .FirstOrDefaultAsync(g => g.Id == objectId, ct);
                if (gcodeFile != null)
                {
                    gcodeFile.Tags.Clear();
                }

                break;

            case "Model3D":
                Model3D? model3d = await _dbContext.Set<Model3D>()
                    .Include(m => m.Tags)
                    .FirstOrDefaultAsync(m => m.Id == objectId, ct);
                if (model3d != null)
                {
                    model3d.Tags.Clear();
                }

                break;
        }
    }

    /// <summary>
    /// Remove a tag from all objects that have it using skip-navigation.
    /// </summary>
    /// <param name="tagId">The unique identifier of the tag to remove from all objects.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task RemoveAllObjectsFromTagAsync(Guid tagId, CancellationToken ct)
    {
        Tag? tag = await _dbContext.Set<Tag>().FirstOrDefaultAsync(t => t.Id == tagId, ct);
        if (tag == null)
        {
            return;
        }

        // Remove from all GcodeFiles
        List<GcodeFile> gcodeFilesWithTag = await _dbContext.GcodeFiles
            .Include(g => g.Tags)
            .Where(g => g.Tags.Any(t => t.Id == tagId))
            .ToListAsync(ct);

        foreach (GcodeFile? gcodeFile in gcodeFilesWithTag)
        {
            Tag? tagToRemove = gcodeFile.Tags.FirstOrDefault(t => t.Id == tagId);
            if (tagToRemove != null)
            {
                gcodeFile.Tags.Remove(tagToRemove);
            }
        }

        // Remove from all Model3Ds
        List<Model3D> models3dWithTag = await _dbContext.Set<Model3D>()
            .Include(m => m.Tags)
            .Where(m => m.Tags.Any(t => t.Id == tagId))
            .ToListAsync(ct);

        foreach (Model3D? model3d in models3dWithTag)
        {
            Tag? tagToRemove = model3d.Tags.FirstOrDefault(t => t.Id == tagId);
            if (tagToRemove != null)
            {
                model3d.Tags.Remove(tagToRemove);
            }
        }
    }

    /// <summary>
    /// Get all tags assigned to an object using skip-navigation.
    /// </summary>
    /// <param name="objectId">The unique identifier of the object to retrieve tags for.</param>
    /// <param name="objectType">The type of object (e.g., "GcodeFile" or "Model3D").</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<IReadOnlyList<Tag>> GetTagsByObjectAsync(Guid objectId, string objectType, CancellationToken ct)
    {
        return objectType switch
        {
            "GcodeFile" => await _dbContext.GcodeFiles
                .Where(g => g.Id == objectId)
                .SelectMany(g => g.Tags)
                .ToListAsync(ct),
            "Model3D" => await _dbContext.Set<Model3D>()
                .Where(m => m.Id == objectId)
                .SelectMany(m => m.Tags)
                .ToListAsync(ct),
            _ => []
        };
    }

    /// <summary>
    /// Get the total count of objects using a specific tag (across both GcodeFile and Model3D).
    /// </summary>
    /// <param name="tagId">The unique identifier of the tag to count usage for.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<int> GetTagUsageCountAsync(Guid tagId, CancellationToken ct)
    {
        int gcodeCount = await _dbContext.GcodeFiles
            .Where(g => g.Tags.Any(t => t.Id == tagId))
            .CountAsync(ct);

        int model3dCount = await _dbContext.Set<Model3D>()
            .Where(m => m.Tags.Any(t => t.Id == tagId))
            .CountAsync(ct);

        return gcodeCount + model3dCount;
    }

    /// <summary>
    /// Get the last time a tag was used (last tagged object's UpdatedAt).
    /// </summary>
    /// <param name="tagId">The unique identifier of the tag to check.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<DateTime?> GetTagLastUsedAtAsync(Guid tagId, CancellationToken ct)
    {
        DateTime? gcodeLastUsed = await _dbContext.GcodeFiles
            .Where(g => g.Tags.Any(t => t.Id == tagId))
            .MaxAsync(g => (DateTime?)g.UpdatedAt, ct);

        DateTime? model3dLastUsed = await _dbContext.Set<Model3D>()
            .Where(m => m.Tags.Any(t => t.Id == tagId))
            .MaxAsync(m => (DateTime?)m.UpdatedAt, ct);

        // Return the most recent
        return gcodeLastUsed.HasValue && model3dLastUsed.HasValue
            ? gcodeLastUsed > model3dLastUsed ? gcodeLastUsed : model3dLastUsed
            : gcodeLastUsed ?? model3dLastUsed;
    }

    /// <summary>
    /// Get all objects of a specific type (for polymorphic filtering).
    /// </summary>
    /// <param name="objectType">The type of object to retrieve (e.g., "GcodeFile" or "Model3D").</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<IReadOnlyList<Guid>> GetAllObjectsOfTypeAsync(string objectType, CancellationToken ct)
    {
        return objectType switch
        {
            "GcodeFile" => await _dbContext.GcodeFiles
                .Select(g => g.Id)
                .ToListAsync(ct),
            "Model3D" => await _dbContext.Set<Model3D>()
                .Select(m => m.Id)
                .ToListAsync(ct),
            _ => []
        };
    }
}
