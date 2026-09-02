using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Tags;

/// <summary>
/// Entity Framework implementation of <see cref="ITagRepository"/>.
/// Uses AppDbContext for GcodeFile tags (skip-navigation) and the explicit
/// <see cref="Model3DTagMapping"/> join entity for Model3D tags (since Model3D
/// has been migrated to <c>Farm.Slicer.Module</c> and no longer carries a Tags
/// navigation property).
/// </summary>
public class EfTagRepository(AppDbContext dbContext, IModel3DQueryProvider? model3DQuery = null) : ITagRepository
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IModel3DQueryProvider? _model3DQuery = model3DQuery;

    // Helper accessor for the join table
    private DbSet<Model3DTagMapping> Model3DTags => _dbContext.Set<Model3DTagMapping>();

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

        bool hasInModel3D = await Model3DTags
            .AnyAsync(x => x.Model3DId == objectId && x.TagsId == tagId, ct);

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

        // Try Model3D via join table (skip when slicer module is disabled)
        if (_model3DQuery is not null
            && !await Model3DTags.AnyAsync(x => x.Model3DId == objectId && x.TagsId == tagId, ct))
        {
            // Verify Model3D exists
            bool exists = await _model3DQuery.ExistsAsync(objectId, ct);
            if (exists)
            {
                Model3DTags.Add(new Model3DTagMapping { Model3DId = objectId, TagsId = tagId });
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

        // Try Model3D via join table
        await Model3DTags
            .Where(x => x.Model3DId == objectId && x.TagsId == tagId)
            .ExecuteDeleteAsync(ct);
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

        List<Tag> model3dTags = await Model3DTags
            .Where(x => x.Model3DId == objectId)
            .Include(x => x.Tag)
            .Select(x => x.Tag!)
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

        // Try Model3D via join table
        await Model3DTags
            .Where(x => x.Model3DId == objectId)
            .ExecuteDeleteAsync(ct);
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
            "Model3D" => await Model3DTags
                .Where(x => x.TagsId == tagId)
                .Select(x => x.Model3DId)
                .ToListAsync(ct),
            "Printer" => await _dbContext.Printers
                .Where(p => p.Tags.Any(t => t.Id == tagId))
                .Select(p => p.Id)
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
                "Model3D" => await Model3DTags
                    .Where(x => tagIdList.Contains(x.TagsId))
                    .GroupBy(x => x.Model3DId)
                    .Where(g => g.Count() == tagIdList.Count)
                    .Select(g => g.Key)
                    .ToListAsync(ct),
                "Printer" => await _dbContext.Printers
                    .Where(p => tagIdList.All(tagId => p.Tags.Any(t => t.Id == tagId)))
                    .Select(p => p.Id)
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
                "Model3D" => await Model3DTags
                    .Where(x => tagIdList.Contains(x.TagsId))
                    .Select(x => x.Model3DId)
                    .Distinct()
                    .ToListAsync(ct),
                "Printer" => await _dbContext.Printers
                    .Where(p => p.Tags.Any(t => tagIdList.Contains(t.Id)))
                    .Select(p => p.Id)
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
            "Model3D" => await Model3DTags
                .AnyAsync(x => x.Model3DId == objectId && x.TagsId == tagId, ct),
            "Printer" => await _dbContext.Printers
                .Where(p => p.Id == objectId)
                .AnyAsync(p => p.Tags.Any(t => t.Id == tagId), ct),
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
                if (_model3DQuery is not null
                    && !await Model3DTags.AnyAsync(x => x.Model3DId == objectId && x.TagsId == tagId, ct))
                {
                    bool model3dExists = await _model3DQuery.ExistsAsync(objectId, ct);
                    if (model3dExists)
                    {
                        Model3DTags.Add(new Model3DTagMapping { Model3DId = objectId, TagsId = tagId });
                    }
                }

                break;

            case "Printer":
                Printer? printerToTag = await _dbContext.Printers
                    .Include(p => p.Tags)
                    .FirstOrDefaultAsync(p => p.Id == objectId, ct);
                if (printerToTag != null && !printerToTag.Tags.Any(t => t.Id == tagId))
                {
                    printerToTag.Tags.Add(tag);
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
                await Model3DTags
                    .Where(x => x.Model3DId == objectId && x.TagsId == tagId)
                    .ExecuteDeleteAsync(ct);
                break;

            case "Printer":
                Printer? printerToUntag = await _dbContext.Printers
                    .Include(p => p.Tags)
                    .FirstOrDefaultAsync(p => p.Id == objectId, ct);
                if (printerToUntag != null)
                {
                    printerToUntag.Tags.Remove(tag);
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
                await Model3DTags
                    .Where(x => x.Model3DId == objectId)
                    .ExecuteDeleteAsync(ct);
                break;

            case "Printer":
                Printer? printerToClear = await _dbContext.Printers
                    .Include(p => p.Tags)
                    .FirstOrDefaultAsync(p => p.Id == objectId, ct);
                if (printerToClear != null)
                {
                    printerToClear.Tags.Clear();
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

        // Remove from all Printers
        List<Printer> printersWithTag = await _dbContext.Printers
            .Include(p => p.Tags)
            .Where(p => p.Tags.Any(t => t.Id == tagId))
            .ToListAsync(ct);

        foreach (Printer? printer in printersWithTag)
        {
            Tag? tagToRemove = printer.Tags.FirstOrDefault(t => t.Id == tagId);
            if (tagToRemove != null)
            {
                printer.Tags.Remove(tagToRemove);
            }
        }

        // Remove from all Model3Ds via join table
        await Model3DTags
            .Where(x => x.TagsId == tagId)
            .ExecuteDeleteAsync(ct);
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
            "Model3D" => await Model3DTags
                .Where(x => x.Model3DId == objectId)
                .Include(x => x.Tag)
                .Select(x => x.Tag!)
                .ToListAsync(ct),
            "Printer" => await _dbContext.Printers
                .Where(p => p.Id == objectId)
                .SelectMany(p => p.Tags)
                .ToListAsync(ct),
            _ => []
        };
    }

    /// <summary>
    /// Get tags for multiple objects of the same type using skip-navigation, in one query
    /// per object type. Replaces N per-object round trips (e.g. per-card printer tag fetches)
    /// with a single grouped lookup. Objects with no tags are omitted from the result map.
    /// </summary>
    /// <param name="objectIds">The unique identifiers of the objects to retrieve tags for.</param>
    /// <param name="objectType">The type of object (e.g., "GcodeFile", "Model3D", or "Printer").</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Tag>>> GetTagsByObjectsAsync(
        IReadOnlyCollection<Guid> objectIds,
        string objectType,
        CancellationToken ct)
    {
        if (objectIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<Tag>>();
        }

        switch (objectType)
        {
            case "GcodeFile":
                List<GcodeFile> gcodeFiles = await _dbContext.GcodeFiles
                    .AsNoTracking()
                    .Where(g => objectIds.Contains(g.Id))
                    .Include(g => g.Tags)
                    .ToListAsync(ct);
                return gcodeFiles.ToDictionary(g => g.Id, g => (IReadOnlyList<Tag>)g.Tags.ToList());

            case "Model3D":
                List<Model3DTagMapping> mappings = await Model3DTags
                    .Where(x => objectIds.Contains(x.Model3DId))
                    .Include(x => x.Tag)
                    .ToListAsync(ct);
                return mappings
                    .GroupBy(x => x.Model3DId)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<Tag>)g.Select(x => x.Tag!).ToList());

            case "Printer":
                List<Printer> printers = await _dbContext.Printers
                    .AsNoTracking()
                    .Where(p => objectIds.Contains(p.Id))
                    .Include(p => p.Tags)
                    .ToListAsync(ct);
                return printers.ToDictionary(p => p.Id, p => (IReadOnlyList<Tag>)p.Tags.ToList());

            default:
                return new Dictionary<Guid, IReadOnlyList<Tag>>();
        }
    }

    /// <summary>
    /// Get the total count of objects using a specific tag (across both GcodeFile and Model3D).
    /// Implemented on top of <see cref="GetTagUsageCountsAsync"/> so single-tag and batch
    /// callers share one set-based query path (issue #2362).
    /// </summary>
    /// <param name="tagId">The unique identifier of the tag to count usage for.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<int> GetTagUsageCountAsync(Guid tagId, CancellationToken ct)
    {
        IReadOnlyDictionary<Guid, int> counts = await GetTagUsageCountsAsync([tagId], ct);
        return counts.TryGetValue(tagId, out int count) ? count : 0;
    }

    /// <summary>
    /// Get the last time a tag was used (last tagged object's UpdatedAt). Implemented on top of
    /// <see cref="GetTagLastUsedAtBatchAsync"/> so single-tag and batch callers share one
    /// set-based query path (issue #2362).
    /// </summary>
    /// <param name="tagId">The unique identifier of the tag to check.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<DateTime?> GetTagLastUsedAtAsync(Guid tagId, CancellationToken ct)
    {
        IReadOnlyDictionary<Guid, DateTime> lastUsed = await GetTagLastUsedAtBatchAsync([tagId], ct);
        return lastUsed.TryGetValue(tagId, out DateTime value) ? value : null;
    }

    /// <summary>
    /// Get usage counts for a set of tags across GcodeFiles, Printers, and Model3D in three
    /// fixed GROUP BY queries, instead of one query per tag (issue #2362). Tags with zero
    /// usage are still present in the result with count 0 — counts are seeded up front and
    /// only incremented by matching GROUP BY rows, so no INNER JOIN can silently drop them.
    /// </summary>
    /// <param name="tagIds">The tag ids to compute usage counts for.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<IReadOnlyDictionary<Guid, int>> GetTagUsageCountsAsync(IReadOnlyCollection<Guid> tagIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tagIds);

        Dictionary<Guid, int> counts = tagIds.ToDictionary(id => id, _ => 0);
        if (tagIds.Count == 0)
        {
            return counts;
        }

        // Query 1: GcodeFile usage, grouped by tag id.
        var gcodeCounts = await _dbContext.GcodeFiles
            .SelectMany(g => g.Tags, (g, t) => t.Id)
            .Where(tagId => tagIds.Contains(tagId))
            .GroupBy(tagId => tagId)
            .Select(g => new { TagId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        foreach (var row in gcodeCounts)
        {
            counts[row.TagId] += row.Count;
        }

        // Query 2: Printer usage, grouped by tag id.
        var printerCounts = await _dbContext.Printers
            .SelectMany(p => p.Tags, (p, t) => t.Id)
            .Where(tagId => tagIds.Contains(tagId))
            .GroupBy(tagId => tagId)
            .Select(g => new { TagId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        foreach (var row in printerCounts)
        {
            counts[row.TagId] += row.Count;
        }

        // Query 3: Model3D usage via the join table, grouped by tag id. Distinct(Model3DId)
        // per group preserves GetTagUsageCountAsync's prior per-tag semantics.
        var model3dCounts = await Model3DTags
            .Where(x => tagIds.Contains(x.TagsId))
            .GroupBy(x => x.TagsId)
            .Select(g => new { TagId = g.Key, Count = g.Select(x => x.Model3DId).Distinct().Count() })
            .ToListAsync(ct);
        foreach (var row in model3dCounts)
        {
            counts[row.TagId] += row.Count;
        }

        return counts;
    }

    /// <summary>
    /// Get the last-used timestamp for a set of tags across GcodeFile and Model3D usage, in a
    /// small fixed number of queries instead of one round trip per tag (issue #2362). Printers
    /// are excluded because <see cref="Farm.Infrastructure.Domain.Printer"/> carries no
    /// UpdatedAt timestamp, matching the prior single-tag semantics.
    /// </summary>
    /// <param name="tagIds">The tag ids to compute last-used timestamps for.</param>
    /// <param name="ct">A cancellation token to observe while waiting for the task to complete.</param>
    public async Task<IReadOnlyDictionary<Guid, DateTime>> GetTagLastUsedAtBatchAsync(IReadOnlyCollection<Guid> tagIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tagIds);

        Dictionary<Guid, DateTime> lastUsed = new();
        if (tagIds.Count == 0)
        {
            return lastUsed;
        }

        // Query 1: GcodeFile side, grouped by tag id.
        var gcodeLastUsed = await _dbContext.GcodeFiles
            .SelectMany(g => g.Tags, (g, t) => new { TagId = t.Id, g.UpdatedAt })
            .Where(x => tagIds.Contains(x.TagId))
            .GroupBy(x => x.TagId)
            .Select(g => new { TagId = g.Key, LastUsedAt = g.Max(x => x.UpdatedAt) })
            .ToListAsync(ct);
        foreach (var row in gcodeLastUsed)
        {
            lastUsed[row.TagId] = row.LastUsedAt;
        }

        // Query 2: Model3D mapping rows (tag id + model id pairs) for the requested tags.
        var model3dMappings = await Model3DTags
            .Where(x => tagIds.Contains(x.TagsId))
            .Select(x => new { x.TagsId, x.Model3DId })
            .ToListAsync(ct);

        if (model3dMappings.Count > 0 && _model3DQuery is not null)
        {
            // Query 3 (cross-module): per-model UpdatedAt for the distinct Model3D ids
            // referenced above, via IModel3DQueryProvider — EfTagRepository must not reach
            // into SlicerDbContext directly (module boundary). The per-tag grouping is then
            // done in memory by joining the mapping rows fetched above against this dictionary.
            List<Guid> distinctModelIds = model3dMappings.Select(m => m.Model3DId).Distinct().ToList();
            IReadOnlyDictionary<Guid, DateTime> modelUpdatedAt = await _model3DQuery.GetUpdatedAtByIdsAsync(distinctModelIds, ct);

            foreach (var mapping in model3dMappings)
            {
                if (modelUpdatedAt.TryGetValue(mapping.Model3DId, out DateTime updatedAt)
                    && (!lastUsed.TryGetValue(mapping.TagsId, out DateTime existing) || updatedAt > existing))
                {
                    lastUsed[mapping.TagsId] = updatedAt;
                }
            }
        }

        return lastUsed;
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
            "Model3D" => _model3DQuery is not null
                ? await _model3DQuery.GetAllIdsAsync(ct)
                : [],
            "Printer" => await _dbContext.Printers
                .Select(p => p.Id)
                .ToListAsync(ct),
            _ => []
        };
    }
}
