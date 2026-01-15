using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Tags
{
    public class EfTagRepository : ITagRepository
    {
        private readonly AppDbContext _dbContext;

        public EfTagRepository(AppDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<Tag?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            return await _dbContext.Tags
                .FirstOrDefaultAsync(t => t.Id == id, ct);
        }

        public async Task<Tag?> GetByNameAsync(string name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // Since tags are normalized to PascalCase on creation, we can do exact matching
            return await _dbContext.Tags
                .FirstOrDefaultAsync(t => t.Name == name, ct);
        }

        public async Task<IReadOnlyList<Tag>> ListAllAsync(CancellationToken ct)
        {
            return await _dbContext.Tags
                .OrderBy(t => t.Name)
                .ToListAsync(ct);
        }

        public async Task AddAsync(Tag tag, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(tag);

            _ = await _dbContext.Tags.AddAsync(tag, ct);
        }

        public async Task RemoveAsync(Tag tag, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(tag);

            _ = _dbContext.Tags.Remove(tag);
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
        public async Task<bool> HasTagAsync(Guid objectId, Guid tagId, CancellationToken ct)
        {
            var hasInGcodeFile = await _dbContext.GcodeFiles
                .Where(g => g.Id == objectId)
                .AnyAsync(g => g.Tags.Any(t => t.Id == tagId), ct);

            if (hasInGcodeFile)
            {
                return true;
            }

            var hasInModel3D = await _dbContext.Models3D
                .Where(m => m.Id == objectId)
                .AnyAsync(m => m.Tags.Any(t => t.Id == tagId), ct);

            return hasInModel3D;
        }

        /// <summary>
        /// Assign a tag to an object (object-agnostic).
        /// Searches both GcodeFile and Model3D to find the object.
        /// </summary>
        public async Task AssignTagAsync(Guid objectId, Guid tagId, CancellationToken ct)
        {
            var tag = await _dbContext.Tags.FirstOrDefaultAsync(t => t.Id == tagId, ct);
            if (tag == null)
            {
                throw new InvalidOperationException($"Tag with ID {tagId} not found.");
            }

            // Try GcodeFile first
            var gcodeFile = await _dbContext.GcodeFiles
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
            var model3d = await _dbContext.Models3D
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
        public async Task RemoveTagAsync(Guid objectId, Guid tagId, CancellationToken ct)
        {
            var tag = await _dbContext.Tags.FirstOrDefaultAsync(t => t.Id == tagId, ct);
            if (tag == null)
            {
                return;
            }

            // Try GcodeFile first
            var gcodeFile = await _dbContext.GcodeFiles
                .Include(g => g.Tags)
                .FirstOrDefaultAsync(g => g.Id == objectId, ct);

            if (gcodeFile != null)
            {
                gcodeFile.Tags.Remove(tag);
                return;
            }

            // Try Model3D
            var model3d = await _dbContext.Models3D
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
        public async Task<IReadOnlyList<Tag>> GetTagsByObjectAsync(Guid objectId, CancellationToken ct)
        {
            var gcodeFileTags = await _dbContext.GcodeFiles
                .Where(g => g.Id == objectId)
                .SelectMany(g => g.Tags)
                .ToListAsync(ct);

            if (gcodeFileTags.Count > 0)
            {
                return gcodeFileTags;
            }

            var model3dTags = await _dbContext.Models3D
                .Where(m => m.Id == objectId)
                .SelectMany(m => m.Tags)
                .ToListAsync(ct);

            return model3dTags;
        }

        /// <summary>
        /// Remove all tags from an object (object-agnostic).
        /// Searches both GcodeFile and Model3D to find the object.
        /// </summary>
        public async Task RemoveAllTagsFromObjectAsync(Guid objectId, CancellationToken ct)
        {
            // Try GcodeFile first
            var gcodeFile = await _dbContext.GcodeFiles
                .Include(g => g.Tags)
                .FirstOrDefaultAsync(g => g.Id == objectId, ct);

            if (gcodeFile != null)
            {
                gcodeFile.Tags.Clear();
                return;
            }

            // Try Model3D
            var model3d = await _dbContext.Models3D
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
        public async Task<IReadOnlyList<Guid>> GetObjectsByTagAsync(Guid tagId, string objectType, CancellationToken ct)
        {
            return objectType switch
            {
                "GcodeFile" => await _dbContext.GcodeFiles
                    .Where(g => g.Tags.Any(t => t.Id == tagId))
                    .Select(g => g.Id)
                    .ToListAsync(ct),
                "Model3D" => await _dbContext.Models3D
                    .Where(m => m.Tags.Any(t => t.Id == tagId))
                    .Select(m => m.Id)
                    .ToListAsync(ct),
                _ => new List<Guid>()
            };
        }

        /// <summary>
        /// Get objects that have ALL of the specified tags (intersection).
        /// </summary>
        public async Task<IReadOnlyList<Guid>> GetObjectsWithAllTagsAsync(IEnumerable<Guid> tagIds, string objectType, CancellationToken ct)
        {
            var tagIdList = tagIds.ToList();
            if (tagIdList.Count == 0)
            {
                return new List<Guid>();
            }

            return objectType switch
            {
                "GcodeFile" => await _dbContext.GcodeFiles
                    .Where(g => tagIdList.All(tagId => g.Tags.Any(t => t.Id == tagId)))
                    .Select(g => g.Id)
                    .ToListAsync(ct),
                "Model3D" => await _dbContext.Models3D
                    .Where(m => tagIdList.All(tagId => m.Tags.Any(t => t.Id == tagId)))
                    .Select(m => m.Id)
                    .ToListAsync(ct),
                _ => new List<Guid>()
            };
        }

        /// <summary>
        /// Get objects that have ANY of the specified tags (union).
        /// </summary>
        public async Task<IReadOnlyList<Guid>> GetObjectsWithAnyTagsAsync(IEnumerable<Guid> tagIds, string objectType, CancellationToken ct)
        {
            var tagIdList = tagIds.ToList();
            if (tagIdList.Count == 0)
            {
                return new List<Guid>();
            }

            return objectType switch
            {
                "GcodeFile" => await _dbContext.GcodeFiles
                    .Where(g => g.Tags.Any(t => tagIdList.Contains(t.Id)))
                    .Select(g => g.Id)
                    .ToListAsync(ct),
                "Model3D" => await _dbContext.Models3D
                    .Where(m => m.Tags.Any(t => tagIdList.Contains(t.Id)))
                    .Select(m => m.Id)
                    .ToListAsync(ct),
                _ => new List<Guid>()
            };
        }

        /// <summary>
        /// Check if an object has a specific tag using skip-navigation.
        /// </summary>
        public async Task<bool> HasTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct)
        {
            return objectType switch
            {
                "GcodeFile" => await _dbContext.GcodeFiles
                    .Where(g => g.Id == objectId)
                    .AnyAsync(g => g.Tags.Any(t => t.Id == tagId), ct),
                "Model3D" => await _dbContext.Models3D
                    .Where(m => m.Id == objectId)
                    .AnyAsync(m => m.Tags.Any(t => t.Id == tagId), ct),
                _ => false
            };
        }

        /// <summary>
        /// Assign a tag to an object by adding it to the skip-navigation collection.
        /// </summary>
        public async Task AssignTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct)
        {
            var tag = await _dbContext.Tags.FirstOrDefaultAsync(t => t.Id == tagId, ct);
            if (tag == null)
            {
                throw new InvalidOperationException($"Tag with ID {tagId} not found.");
            }

            switch (objectType)
            {
                case "GcodeFile":
                    var gcodeFile = await _dbContext.GcodeFiles
                        .Include(g => g.Tags)
                        .FirstOrDefaultAsync(g => g.Id == objectId, ct);
                    if (gcodeFile != null && !gcodeFile.Tags.Any(t => t.Id == tagId))
                    {
                        gcodeFile.Tags.Add(tag);
                    }
                    break;

                case "Model3D":
                    var model3d = await _dbContext.Models3D
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
        public async Task RemoveTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct)
        {
            var tag = await _dbContext.Tags.FirstOrDefaultAsync(t => t.Id == tagId, ct);
            if (tag == null)
            {
                return;
            }

            switch (objectType)
            {
                case "GcodeFile":
                    var gcodeFile = await _dbContext.GcodeFiles
                        .Include(g => g.Tags)
                        .FirstOrDefaultAsync(g => g.Id == objectId, ct);
                    if (gcodeFile != null)
                    {
                        gcodeFile.Tags.Remove(tag);
                    }
                    break;

                case "Model3D":
                    var model3d = await _dbContext.Models3D
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
        public async Task RemoveAllTagsFromObjectAsync(Guid objectId, string objectType, CancellationToken ct)
        {
            switch (objectType)
            {
                case "GcodeFile":
                    var gcodeFile = await _dbContext.GcodeFiles
                        .Include(g => g.Tags)
                        .FirstOrDefaultAsync(g => g.Id == objectId, ct);
                    if (gcodeFile != null)
                    {
                        gcodeFile.Tags.Clear();
                    }
                    break;

                case "Model3D":
                    var model3d = await _dbContext.Models3D
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
        public async Task RemoveAllObjectsFromTagAsync(Guid tagId, CancellationToken ct)
        {
            var tag = await _dbContext.Tags.FirstOrDefaultAsync(t => t.Id == tagId, ct);
            if (tag == null)
            {
                return;
            }

            // Remove from all GcodeFiles
            var gcodeFilesWithTag = await _dbContext.GcodeFiles
                .Include(g => g.Tags)
                .Where(g => g.Tags.Any(t => t.Id == tagId))
                .ToListAsync(ct);

            foreach (var gcodeFile in gcodeFilesWithTag)
            {
                var tagToRemove = gcodeFile.Tags.FirstOrDefault(t => t.Id == tagId);
                if (tagToRemove != null)
                {
                    gcodeFile.Tags.Remove(tagToRemove);
                }
            }

            // Remove from all Model3Ds
            var models3dWithTag = await _dbContext.Models3D
                .Include(m => m.Tags)
                .Where(m => m.Tags.Any(t => t.Id == tagId))
                .ToListAsync(ct);

            foreach (var model3d in models3dWithTag)
            {
                var tagToRemove = model3d.Tags.FirstOrDefault(t => t.Id == tagId);
                if (tagToRemove != null)
                {
                    model3d.Tags.Remove(tagToRemove);
                }
            }
        }

        /// <summary>
        /// Get all tags assigned to an object using skip-navigation.
        /// </summary>
        public async Task<IReadOnlyList<Tag>> GetTagsByObjectAsync(Guid objectId, string objectType, CancellationToken ct)
        {
            return objectType switch
            {
                "GcodeFile" => await _dbContext.GcodeFiles
                    .Where(g => g.Id == objectId)
                    .SelectMany(g => g.Tags)
                    .ToListAsync(ct),
                "Model3D" => await _dbContext.Models3D
                    .Where(m => m.Id == objectId)
                    .SelectMany(m => m.Tags)
                    .ToListAsync(ct),
                _ => new List<Tag>()
            };
        }

        /// <summary>
        /// Get the total count of objects using a specific tag (across both GcodeFile and Model3D).
        /// </summary>
        public async Task<int> GetTagUsageCountAsync(Guid tagId, CancellationToken ct)
        {
            int gcodeCount = await _dbContext.GcodeFiles
                .Where(g => g.Tags.Any(t => t.Id == tagId))
                .CountAsync(ct);

            int model3dCount = await _dbContext.Models3D
                .Where(m => m.Tags.Any(t => t.Id == tagId))
                .CountAsync(ct);

            return gcodeCount + model3dCount;
        }

        /// <summary>
        /// Get the last time a tag was used (last tagged object's UpdatedAt).
        /// </summary>
        public async Task<DateTime?> GetTagLastUsedAtAsync(Guid tagId, CancellationToken ct)
        {
            var gcodeLastUsed = await _dbContext.GcodeFiles
                .Where(g => g.Tags.Any(t => t.Id == tagId))
                .MaxAsync(g => (DateTime?)g.UpdatedAt, ct);

            var model3dLastUsed = await _dbContext.Models3D
                .Where(m => m.Tags.Any(t => t.Id == tagId))
                .MaxAsync(m => (DateTime?)m.UpdatedAt, ct);

            // Return the most recent
            if (gcodeLastUsed.HasValue && model3dLastUsed.HasValue)
            {
                return gcodeLastUsed > model3dLastUsed ? gcodeLastUsed : model3dLastUsed;
            }
            
            return gcodeLastUsed ?? model3dLastUsed;
        }

        /// <summary>
        /// Get all objects of a specific type (for polymorphic filtering).
        /// </summary>
        public async Task<IReadOnlyList<Guid>> GetAllObjectsOfTypeAsync(string objectType, CancellationToken ct)
        {
            return objectType switch
            {
                "GcodeFile" => await _dbContext.GcodeFiles
                    .Select(g => g.Id)
                    .ToListAsync(ct),
                "Model3D" => await _dbContext.Models3D
                    .Select(m => m.Id)
                    .ToListAsync(ct),
                _ => new List<Guid>()
            };
        }
    }
}
