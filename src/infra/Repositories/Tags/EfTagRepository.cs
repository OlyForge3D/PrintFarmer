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
    }
}
