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

        public async Task<Model3DTagMapping?> GetMappingAsync(Guid modelId, Guid tagId, CancellationToken ct)
        {
            return await _dbContext.Model3DTagMappings
                .FirstOrDefaultAsync(m => m.Model3DId == modelId && m.TagId == tagId, ct);
        }

        public async Task AddAsync(Model3DTagMapping mapping, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(mapping);

            await _dbContext.Model3DTagMappings.AddAsync(mapping, ct);
        }

        public async Task RemoveAsync(Model3DTagMapping mapping, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(mapping);

            _dbContext.Model3DTagMappings.Remove(mapping);
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

        public async Task RemoveByModelAndTagAsync(Guid modelId, Guid tagId, CancellationToken ct)
        {
            Model3DTagMapping? mapping = await GetMappingAsync(modelId, tagId, ct);
            if (mapping != null)
            {
                _dbContext.Model3DTagMappings.Remove(mapping);
            }
        }

        public async Task SaveChangesAsync(CancellationToken ct)
        {
            await _dbContext.SaveChangesAsync(ct);
        }
    }
}
