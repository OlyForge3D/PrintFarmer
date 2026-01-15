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
    /// <summary>
    /// Entity Framework implementation of the generic TagMapping repository.
    /// Supports polymorphic tagging for any object type via the ObjectType discriminator.
    /// </summary>
    public class EfTagMappingRepository : ITagMappingRepository
    {
        private readonly AppDbContext _dbContext;

        public EfTagMappingRepository(AppDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<TagMapping?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            return await _dbContext.TagMappings
                .FirstOrDefaultAsync(m => m.Id == id, ct);
        }

        public async Task<TagMapping?> GetMappingAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct)
        {
            return await _dbContext.TagMappings
                .FirstOrDefaultAsync(m => m.ObjectType == objectType && m.ObjectId == objectId && m.TagId == tagId, ct);
        }

        public async Task<IReadOnlyList<TagMapping>> GetObjectsWithTagsAsync(IEnumerable<Guid> tagIds, string objectType, bool requireAllTags, CancellationToken ct)
        {
            var tagIdList = tagIds.ToList();
            if (!tagIdList.Any())
            {
                return new List<TagMapping>();
            }

            IQueryable<TagMapping> query = _dbContext.TagMappings
                .Where(m => m.ObjectType == objectType && tagIdList.Contains(m.TagId));

            if (requireAllTags)
            {
                var objectIds = await query
                    .GroupBy(m => m.ObjectId)
                    .Where(g => g.Count() == tagIdList.Count)
                    .Select(g => g.Key)
                    .ToListAsync(ct);

                return await _dbContext.TagMappings
                    .Where(m => objectIds.Contains(m.ObjectId) && m.ObjectType == objectType)
                    .OrderBy(m => m.TaggedAt)
                    .ToListAsync(ct);
            }

            return await query
                .OrderBy(m => m.TaggedAt)
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<TagMapping>> GetObjectsExcludingTagsAsync(IEnumerable<Guid> tagIds, string objectType, CancellationToken ct)
        {
            var tagIdList = tagIds.ToList();
            if (!tagIdList.Any())
            {
                return await GetAllObjectsOfTypeAsync(objectType, ct);
            }

            var objectIdsToExclude = await _dbContext.TagMappings
                .Where(m => m.ObjectType == objectType && tagIdList.Contains(m.TagId))
                .Select(m => m.ObjectId)
                .Distinct()
                .ToListAsync(ct);

            return await _dbContext.TagMappings
                .Where(m => m.ObjectType == objectType && !objectIdsToExclude.Contains(m.ObjectId))
                .OrderBy(m => m.TaggedAt)
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<TagMapping>> GetAllObjectsOfTypeAsync(string objectType, CancellationToken ct)
        {
            return await _dbContext.TagMappings
                .Where(m => m.ObjectType == objectType)
                .OrderBy(m => m.TaggedAt)
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<TagMapping>> GetObjectsByTagAsync(Guid tagId, string objectType, CancellationToken ct)
        {
            return await _dbContext.TagMappings
                .Where(m => m.TagId == tagId && m.ObjectType == objectType)
                .OrderBy(m => m.TaggedAt)
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<TagMapping>> GetByObjectAsync(Guid objectId, string objectType, CancellationToken ct)
        {
            return await _dbContext.TagMappings
                .Where(m => m.ObjectType == objectType && m.ObjectId == objectId)
                .OrderBy(m => m.TaggedAt)
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<TagMapping>> GetByTagIdAsync(Guid tagId, CancellationToken ct)
        {
            return await _dbContext.TagMappings
                .Where(m => m.TagId == tagId)
                .OrderBy(m => m.TaggedAt)
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<TagMapping>> GetByTagIdAndObjectTypeAsync(Guid tagId, string objectType, CancellationToken ct)
        {
            return await _dbContext.TagMappings
                .Where(m => m.TagId == tagId && m.ObjectType == objectType)
                .OrderBy(m => m.TaggedAt)
                .ToListAsync(ct);
        }

        public async Task AddAsync(TagMapping mapping, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(mapping);
            _ = await _dbContext.TagMappings.AddAsync(mapping, ct);
        }

        public async Task RemoveAsync(TagMapping mapping, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(mapping);
            _ = _dbContext.TagMappings.Remove(mapping);
            await Task.CompletedTask; // Repository pattern consistency
        }

        public async Task RemoveByObjectAsync(string objectType, Guid objectId, CancellationToken ct)
        {
            List<TagMapping> mappings = await _dbContext.TagMappings
                .Where(m => m.ObjectType == objectType && m.ObjectId == objectId)
                .ToListAsync(ct);

            if (mappings.Any())
            {
                _dbContext.TagMappings.RemoveRange(mappings);
            }
        }

        public async Task RemoveByTagAsync(Guid tagId, CancellationToken ct)
        {
            List<TagMapping> mappings = await _dbContext.TagMappings
                .Where(m => m.TagId == tagId)
                .ToListAsync(ct);

            if (mappings.Any())
            {
                _dbContext.TagMappings.RemoveRange(mappings);
            }
        }

        public async Task RemoveByObjectAndTagAsync(Guid objectId, Guid tagId, string objectType, CancellationToken ct)
        {
            var mapping = await GetMappingAsync(objectId, tagId, objectType, ct);
            if (mapping != null)
            {
                _dbContext.TagMappings.Remove(mapping);
            }
        }

        public async Task SaveChangesAsync(CancellationToken ct)
        {
            await _dbContext.SaveChangesAsync(ct);
        }
    }
}
