using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Model
{
    public class EfModelRepository : IModelRepository
    {
        private readonly AppDbContext _db;

        public EfModelRepository(AppDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task AddAsync(Model3D model, CancellationToken ct)
        {
            _ = await _db.Models3D.AddAsync(model, ct);
        }

        public async Task<Model3D?> GetByHashAsync(string fileHash, CancellationToken ct)
        {
            return await _db.Models3D.FirstOrDefaultAsync(m => m.FileHash == fileHash, ct);
        }

        public async Task<Model3D?> GetByIdAsync(Guid id, CancellationToken ct)
        {
            return await _db.Models3D.FirstOrDefaultAsync(m => m.Id == id && m.IsValid, ct);
        }

        public async Task<Model3D?> GetByIdWithTagsAsync(Guid id, CancellationToken ct)
        {
            return await _db.Models3D
                .Where(m => m.Id == id && m.IsValid)
                .Include(m => m.TagMappings)
                .ThenInclude(tm => tm.Tag)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<IReadOnlyList<Model3D>> ListValidAsync(CancellationToken ct)
        {
            return await _db.Models3D.Where(m => m.IsValid).OrderByDescending(m => m.UploadedAt).ToListAsync(ct);
        }

        public async Task<int> CountValidAsync(CancellationToken ct)
        {
            return await _db.Models3D.Where(m => m.IsValid).CountAsync(ct);
        }

        public async Task<IReadOnlyList<Model3D>> SearchAsync(string? query, Guid[]? tagIds, string sortBy, bool descending, int skip, int take, CancellationToken ct)
        {
            IQueryable<Model3D> queryable = _db.Models3D.Where(m => m.IsValid).AsQueryable();

            // Text search
            if (!string.IsNullOrWhiteSpace(query))
            {
                string searchTerm = query.ToLower();
                queryable = queryable.Where(m => m.DisplayName.Contains(searchTerm, StringComparison.CurrentCultureIgnoreCase) ||
                                                (m.Description != null && m.Description.Contains(searchTerm, StringComparison.CurrentCultureIgnoreCase)));
            }

            // Tag filtering (AND logic - must have all tags)
            if (tagIds?.Length > 0)
            {
                foreach (Guid tagId in tagIds)
                {
                    queryable = queryable.Where(m => m.TagMappings.Any(tm => tm.TagId == tagId));
                }
            }

            // Sorting
            queryable = (sortBy?.ToLower()) switch
            {
                "name" => descending ? queryable.OrderByDescending(m => m.DisplayName) : queryable.OrderBy(m => m.DisplayName),
                "size" => descending ? queryable.OrderByDescending(m => m.FileSizeBytes) : queryable.OrderBy(m => m.FileSizeBytes),
                _ => descending ? queryable.OrderByDescending(m => m.UploadedAt) : queryable.OrderBy(m => m.UploadedAt)
            };

            return await queryable
                .Skip(skip)
                .Take(take)
                .Include(m => m.TagMappings)
                .ThenInclude(tm => tm.Tag)
                .ToListAsync(ct);
        }

        public async Task RemoveAsync(Model3D model, CancellationToken ct)
        {
            _ = _db.Models3D.Remove(model);
            await Task.CompletedTask;
        }

        public async Task UpdateAsync(Model3D model, CancellationToken ct)
        {
            _ = _db.Models3D.Update(model);
            await Task.CompletedTask;
        }

        public async Task SaveChangesAsync(CancellationToken ct)
        {
            _ = await _db.SaveChangesAsync(ct);
        }
    }
}
