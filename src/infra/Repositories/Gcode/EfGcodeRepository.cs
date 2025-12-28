using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Gcode
{
    public class EfGcodeRepository : IGcodeRepository
    {
        private readonly AppDbContext _db;

        public EfGcodeRepository(AppDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<List<GcodeFile>> QueryLibraryAsync(string? search, string? material, double? nozzleDiameter, Guid? targetPrinterId, CancellationToken ct)
        {
            // Load all files with includes first (required for SQLite compatibility with string.Contains)
            List<GcodeFile> allFiles = await _db.GcodeFiles
                .Include(g => g.SourcePrinter)
                .Include(g => g.TargetPrinter)
                .Include(g => g.TargetModel)
                .ToListAsync(ct);

            // Apply client-side filtering for case-insensitive search
            var query = allFiles.AsEnumerable();

            if (!string.IsNullOrEmpty(search))
            {
                var searchLower = search.ToLowerInvariant();
                query = query.Where(g =>
                    (g.OriginalFileName?.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (g.DisplayName?.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (g.Description != null && g.Description.Contains(searchLower, StringComparison.OrdinalIgnoreCase)));
            }

            if (!string.IsNullOrEmpty(material))
            {
                query = query.Where(g => g.RequiredMaterial == material);
            }

            if (nozzleDiameter.HasValue)
            {
                double nd = nozzleDiameter.Value;
                query = query.Where(g => g.RequiredNozzleDiameter != null && Math.Abs(g.RequiredNozzleDiameter.Value - nd) < 0.001);
            }

            if (targetPrinterId.HasValue)
            {
                query = query.Where(g => g.TargetPrinterId == targetPrinterId.Value);
            }

            return query.OrderByDescending(g => g.UploadedAt).ToList();
        }

        public Task<GcodeFile?> GetByIdWithIncludesAsync(Guid id, CancellationToken ct)
        {
            return _db.GcodeFiles
                .Include(g => g.SourcePrinter)
                .Include(g => g.TargetPrinter)
                .Include(g => g.TargetModel)
                .FirstOrDefaultAsync(g => g.Id == id, ct);
        }

        public Task<GcodeFile?> FindByHashAsync(string hash, CancellationToken ct)
        {
            return _db.GcodeFiles.FirstOrDefaultAsync(g => g.FileHash == hash, ct);
        }

        public Task<GcodeFile?> GetByFullPathAsync(string fullPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return Task.FromResult<GcodeFile?>(null);
            }
            return _db.GcodeFiles.FirstOrDefaultAsync(g => g.FilePath == fullPath, ct);
        }

        public async Task<List<GcodeFile>> GetByFullPathsAsync(IEnumerable<string> fullPaths, CancellationToken ct)
        {
            var pathList = fullPaths.ToList();
            if (pathList.Count == 0)
            {
                return new List<GcodeFile>();
            }
            return await _db.GcodeFiles
                .Where(g => pathList.Contains(g.FilePath))
                .ToListAsync(ct);
        }

        public async Task<List<GcodeFile>> ListByDirectoryPrefixAsync(string directoryPrefix, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(directoryPrefix))
            {
                return new List<GcodeFile>();
            }
            // Query all files where FilePath starts with the directory prefix
            // Normalize the path separator for consistent matching
            string normalizedPrefix = directoryPrefix.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return await _db.GcodeFiles
                .Where(g => g.FilePath.StartsWith(normalizedPrefix))
                .ToListAsync(ct);
        }

        public async Task<List<string>> ListSubdirectoriesAsync(string parentDirectory, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                parentDirectory = string.Empty;
            }

            // Normalize parent directory - ensure no trailing separators
            string normalizedParent = parentDirectory.TrimEnd(Path.DirectorySeparatorChar);
            if (normalizedParent.EndsWith('/'))
            {
                normalizedParent = normalizedParent[..^1];
            }

            // Get all unique subdirectories from Folder entities for gcode files
            var subdirs = await _db.Folders
                .Where(f => f.FolderType == "gcode" && !f.DeletedAt.HasValue && f.Path.StartsWith(normalizedParent))
                .Select(f => f.Path)
                .Distinct()
                .ToListAsync(ct);

            // Filter to only direct children (one level down)
            var directChildren = new HashSet<string>();
            foreach (var dir in subdirs)
            {
                // Skip if this is the same as parent (files directly in this directory)
                if (dir == normalizedParent)
                {
                    continue;
                }

                // If parent is empty, we want top-level directories
                if (string.IsNullOrEmpty(normalizedParent))
                {
                    // Split on both / and \ to handle any path separator
                    var segments = dir.Split(new[] { '/', Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length > 0)
                    {
                        directChildren.Add(segments[0]);
                    }
                }
                else
                {
                    // Check if this directory starts with parent + separator
                    if (dir.StartsWith(normalizedParent + "/") || dir.StartsWith(normalizedParent + Path.DirectorySeparatorChar))
                    {
                        // Extract the relative path
                        string relative = dir.Substring(normalizedParent.Length + 1);

                        // Get the first segment of the relative path
                        var segments = relative.Split(new[] { '/', Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                        if (segments.Length > 0)
                        {
                            directChildren.Add(segments[0]);
                        }
                    }
                }
            }

            return directChildren.Distinct().OrderBy(d => d).ToList();
        }

        public async Task<List<GcodeFile>> ListFilesInDirectoryAsync(string directory, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = string.Empty;
            }

            // Get files by finding the folder with matching path
            return await _db.GcodeFiles
                .Where(g => g.Folder != null && g.Folder.Path == directory)
                .ToListAsync(ct);
        }

        public async Task<Guid?> GetLatestHarvestOperationIdForPrinterAsync(Guid printerId, CancellationToken ct)
        {
            GcodeHarvestOperation? op = await _db.GcodeHarvestOperations
                .Where(o => o.PrinterId == printerId)
                .OrderByDescending(o => o.StartedAt)
                .FirstOrDefaultAsync(ct);
            return op?.Id;
        }

        public async Task<Dictionary<Guid, Guid?>> GetLatestHarvestOperationIdsByPrintersAsync(IEnumerable<Guid> printerIds, CancellationToken ct)
        {
            var printerIdList = printerIds.ToList();
            if (printerIdList.Count == 0)
            {
                return new Dictionary<Guid, Guid?>();
            }

            var latestOps = await _db.GcodeHarvestOperations
                .Where(o => printerIdList.Contains(o.PrinterId))
                .GroupBy(o => o.PrinterId)
                .Select(g => new { PrinterId = g.Key, LatestOpId = g.OrderByDescending(o => o.StartedAt).First().Id })
                .ToListAsync(ct);

            var result = new Dictionary<Guid, Guid?>();
            foreach (var printerId in printerIdList)
            {
                var op = latestOps.FirstOrDefault(o => o.PrinterId == printerId);
                result[printerId] = op?.LatestOpId;
            }
            return result;
        }

        public Task AddAsync(GcodeFile file, CancellationToken ct)
        {
            _ = _db.GcodeFiles.Add(file);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(GcodeFile file, CancellationToken ct)
        {
            _ = _db.GcodeFiles.Remove(file);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
    }
}
