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

        public async Task<List<Model3D>> ListValidByDirectoryAsync(string directory, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = string.Empty;
            }

            // Get valid models where FileDirectory exactly matches (not recursive)
            return await _db.Models3D
                .Where(m => m.IsValid && m.FileDirectory == directory)
                .OrderByDescending(m => m.UploadedAt)
                .ToListAsync(ct);
        }

        public async Task<List<string>> ListSubdirectoriesAsync(string parentDirectory, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                parentDirectory = string.Empty;
            }

            string normalizedParent = parentDirectory.TrimEnd(Path.DirectorySeparatorChar);

            // Get all unique subdirectories that are direct children of the parent
            var subdirs = await _db.Models3D
                .Where(m => m.IsValid && m.FileDirectory.StartsWith(normalizedParent))
                .Select(m => m.FileDirectory)
                .Distinct()
                .ToListAsync(ct);

            // Filter to only direct children (one level down)
            var directChildren = new HashSet<string>();
            foreach (var dir in subdirs)
            {
                // If parent is empty, we want top-level directories
                if (string.IsNullOrEmpty(normalizedParent))
                {
                    // Find the first segment
                    var segments = dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length > 0)
                    {
                        directChildren.Add(segments[0]);
                    }
                }
                else
                {
                    // Check if this directory is a direct child of parent
                    if (dir.StartsWith(normalizedParent + Path.DirectorySeparatorChar))
                    {
                        string relative = dir.Substring(normalizedParent.Length).TrimStart(Path.DirectorySeparatorChar);
                        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                        if (segments.Length > 0)
                        {
                            directChildren.Add(segments[0]);
                        }
                    }
                }
            }

            return directChildren.OrderBy(d => d).ToList();
        }

        public async Task<int> CountValidAsync(CancellationToken ct)
        {
            return await _db.Models3D.Where(m => m.IsValid).CountAsync(ct);
        }

        public async Task<IReadOnlyList<Model3D>> SearchAsync(string? query, Guid[]? tagIds, string sortBy, bool descending, int skip, int take, CancellationToken ct)
        {
            // Load all valid models with includes first (required for SQLite compatibility with case-insensitive Contains)
            List<Model3D> allModels = await _db.Models3D
                .Where(m => m.IsValid)
                .Include(m => m.TagMappings)
                .ThenInclude(tm => tm.Tag)
                .ToListAsync(ct);

            // Apply client-side filtering for text search and tags
            var queryable = allModels.AsEnumerable();

            // Text search (case-insensitive)
            if (!string.IsNullOrWhiteSpace(query))
            {
                string searchTerm = query.ToLowerInvariant();
                queryable = queryable.Where(m => 
                    m.DisplayName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                    (m.Description != null && m.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)));
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

            return queryable
                .Skip(skip)
                .Take(take)
                .ToList();
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
