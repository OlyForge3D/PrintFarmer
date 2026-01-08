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
    public class EfModelTagMappingRepository : IModelTagMappingRepository
    {
        private readonly AppDbContext _dbContext;

        public EfModelTagMappingRepository(AppDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<Model3DTagMapping?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            return await _dbContext.Model3DTagMappings
                .FirstOrDefaultAsync(m => m.Id == id, ct);
        }

        public async Task<IReadOnlyList<Model3DTagMapping>> GetByModelIdAsync(Guid modelId, CancellationToken ct)
        {
            return await _dbContext.Model3DTagMappings
                .Where(m => m.Model3DId == modelId)
                .OrderBy(m => m.TaggedAt)
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyList<Model3DTagMapping>> GetByTagIdAsync(Guid tagId, CancellationToken ct)
        {
            return await _dbContext.Model3DTagMappings
                .Where(m => m.TagId == tagId)
                .OrderBy(m => m.TaggedAt)
                .ToListAsync(ct);
        }

        public async Task<Model3DTagMapping?> GetMappingAsync(Guid modelId, Guid tagId, CancellationToken ct)
        {
            return await _dbContext.Model3DTagMappings
                .FirstOrDefaultAsync(m => m.Model3DId == modelId && m.TagId == tagId, ct);
        }

        public async Task AddAsync(Model3DTagMapping mapping, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(mapping);

            _ = await _dbContext.Model3DTagMappings.AddAsync(mapping, ct);
        }

        public async Task RemoveAsync(Model3DTagMapping mapping, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(mapping);

            _ = _dbContext.Model3DTagMappings.Remove(mapping);
            await Task.CompletedTask; // Repository pattern consistency
        }

        public async Task RemoveByModelIdAsync(Guid modelId, CancellationToken ct)
        {
            List<Model3DTagMapping> mappings = await _dbContext.Model3DTagMappings
                .Where(m => m.Model3DId == modelId)
                .ToListAsync(ct);

            if (mappings.Any())
            {
                _dbContext.Model3DTagMappings.RemoveRange(mappings);
            }
        }

        public async Task RemoveByTagIdAsync(Guid tagId, CancellationToken ct)
        {
            List<Model3DTagMapping> mappings = await _dbContext.Model3DTagMappings
                .Where(m => m.TagId == tagId)
                .ToListAsync(ct);

            if (mappings.Any())
            {
                _dbContext.Model3DTagMappings.RemoveRange(mappings);
            }
        }

        public async Task RemoveByModelAndTagAsync(Guid modelId, Guid tagId, CancellationToken ct)
        {
            Model3DTagMapping? mapping = await GetMappingAsync(modelId, tagId, ct);
            if (mapping != null)
            {
                _ = _dbContext.Model3DTagMappings.Remove(mapping);
            }
        }

        public async Task SaveChangesAsync(CancellationToken ct)
        {
            _ = await _dbContext.SaveChangesAsync(ct);
        }

        public async Task<IReadOnlyCollection<Guid>> GetAllModelsAsync(CancellationToken ct)
        {
            return await _dbContext.Model3DTagMappings
                .Select(m => m.Model3DId)
                .Distinct()
                .ToListAsync(ct);
        }

        public async Task<IReadOnlyCollection<Guid>> GetModelsWithTagsAsync(
            IEnumerable<Guid> tagIds,
            bool requireAll,
            CancellationToken ct)
        {
            var tagIdList = tagIds?.ToList() ?? new List<Guid>();
            
            if (tagIdList.Count == 0)
            {
                return Array.Empty<Guid>();
            }

            IQueryable<Guid> query = _dbContext.Model3DTagMappings
                .Where(m => tagIdList.Contains(m.TagId))
                .Select(m => m.Model3DId);

            if (requireAll)
            {
                // Require ALL tags: group by model and count, then filter by count
                var modelIds = await _dbContext.Model3DTagMappings
                    .Where(m => tagIdList.Contains(m.TagId))
                    .GroupBy(m => m.Model3DId)
                    .Where(g => g.Select(m => m.TagId).Distinct().Count() == tagIdList.Count)
                    .Select(g => g.Key)
                    .ToListAsync(ct);

                return modelIds;
            }
            else
            {
                // Require ANY tag: just get distinct model IDs
                return await query.Distinct().ToListAsync(ct);
            }
        }

        public async Task<IReadOnlyCollection<Guid>> GetModelsExcludingTagsAsync(
            IEnumerable<Guid> tagIds,
            CancellationToken ct)
        {
            var tagIdList = tagIds?.ToList() ?? new List<Guid>();
            
            if (tagIdList.Count == 0)
            {
                // No tags to exclude - return all models
                return await GetAllModelsAsync(ct);
            }

            // Get all models
            var allModels = await GetAllModelsAsync(ct);

            // Get models that HAVE the excluded tags
            var modelsWithExcludedTags = await _dbContext.Model3DTagMappings
                .Where(m => tagIdList.Contains(m.TagId))
                .Select(m => m.Model3DId)
                .Distinct()
                .ToListAsync(ct);

            var excludedSet = new HashSet<Guid>(modelsWithExcludedTags);

            // Return models that DON'T have any excluded tags
            return allModels.Where(m => !excludedSet.Contains(m)).ToList();
        }
    }
}
