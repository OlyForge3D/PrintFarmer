using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Repositories.Tags
{
    public class EfTagRepository : ITagRepository
    {
        private readonly AppDbContext _dbContext;

        public EfTagRepository(AppDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<Model3DTag?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            return await _dbContext.Model3DTags
                .FirstOrDefaultAsync(t => t.Id == id, ct);
        }

        public async Task<Model3DTag?> GetByNameAsync(string name, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // Since tags are normalized to PascalCase on creation, we can do exact matching
            return await _dbContext.Model3DTags
                .FirstOrDefaultAsync(t => t.Name == name, ct);
        }

        public async Task<IReadOnlyList<Model3DTag>> ListAllAsync(CancellationToken ct)
        {
            return await _dbContext.Model3DTags
                .OrderBy(t => t.Name)
                .ToListAsync(ct);
        }

        public async Task AddAsync(Model3DTag tag, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(tag);

            await _dbContext.Model3DTags.AddAsync(tag, ct);
        }

        public async Task RemoveAsync(Model3DTag tag, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(tag);

            _dbContext.Model3DTags.Remove(tag);
            await Task.CompletedTask; // Repository pattern consistency
        }

        public async Task SaveChangesAsync(CancellationToken ct)
        {
            await _dbContext.SaveChangesAsync(ct);
        }
    }
}
