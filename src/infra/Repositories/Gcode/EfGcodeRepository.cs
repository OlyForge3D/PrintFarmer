using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Gcode;

public class EfGcodeRepository(AppDbContext db) : IGcodeRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<List<GcodeFile>> QueryLibraryAsync(string? search, string? material, double? nozzleDiameter, Guid? printerModelId, CancellationToken ct)
    {
        IQueryable<GcodeFile> query = _db.GcodeFiles.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
#pragma warning disable CA1862 // Database providers translate ToLower; StringComparison overloads do not translate consistently.
            string searchLower = search.Trim().ToLowerInvariant();
            query = query.Where(g =>
                g.FileName.ToLower().Contains(searchLower) ||
                (g.Description != null && g.Description.ToLower().Contains(searchLower)));
#pragma warning restore CA1862
        }

        if (!string.IsNullOrWhiteSpace(material))
        {
            query = query.Where(g => g.RequiredMaterial == material);
        }

        if (nozzleDiameter.HasValue)
        {
            double nd = nozzleDiameter.Value;
            double minNozzleDiameter = nd - 0.001;
            double maxNozzleDiameter = nd + 0.001;
            query = query.Where(g => g.RequiredNozzleDiameter >= minNozzleDiameter && g.RequiredNozzleDiameter <= maxNozzleDiameter);
        }

        if (printerModelId.HasValue)
        {
            query = query.Where(g => g.PrinterModelId == printerModelId.Value);
        }

        return await query
            .Include(g => g.SourcePrinter)
            .Include(g => g.PrinterModel)
            .Include(g => g.Tags)
            .OrderByDescending(g => g.UploadedAt)
            .ToListAsync(ct);
    }

    public Task<GcodeFile?> GetByIdWithIncludesAsync(Guid id, CancellationToken ct)
    {
        return _db.GcodeFiles
            .Include(g => g.SourcePrinter)
            .Include(g => g.PrinterModel)
            .Include(g => g.Tags)
            .FirstOrDefaultAsync(g => g.Id == id, ct);
    }

    public Task<GcodeFile?> FindByHashAsync(string hash, CancellationToken ct)
    {
        return _db.GcodeFiles.FirstOrDefaultAsync(g => g.FileHash == hash, ct);
    }

    public Task<GcodeFile?> GetByFullPathAsync(string fullPath, CancellationToken ct)
    {
        return string.IsNullOrWhiteSpace(fullPath)
            ? Task.FromResult<GcodeFile?>(null)
            : _db.GcodeFiles.FirstOrDefaultAsync(g => g.FilePath == fullPath, ct);
    }

    public async Task<List<GcodeFile>> GetByFullPathsAsync(IEnumerable<string> fullPaths, CancellationToken ct)
    {
        var pathList = fullPaths.ToList();
        return pathList.Count == 0
            ? new List<GcodeFile>()
            : await _db.GcodeFiles
            .Where(g => pathList.Contains(g.FilePath))
            .ToListAsync(ct);
    }

    public async Task<List<GcodeFile>> ListByDirectoryPrefixAsync(string directoryPrefix, CancellationToken ct)
    {
        // If prefix is null/empty, return ALL files (for grid view "show all files" mode)
        if (string.IsNullOrWhiteSpace(directoryPrefix))
        {
            return await _db.GcodeFiles.ToListAsync(ct);
        }

        // Query all files where FilePath starts with the directory prefix
        // Normalize the path separator for consistent matching
        string normalizedPrefix = directoryPrefix.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return await _db.GcodeFiles
            .Where(g => g.FilePath.StartsWith(normalizedPrefix))
            .ToListAsync(ct);
    }

    public async Task<(List<GcodeFile> Files, int TotalCount)> QueryFilesAsync(
        string? path,
        string? search,
        Guid[]? tagIds,
        Guid? printerModelId,
        Guid? printerId,
        string? sortBy,
        string? sortOrder,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        // Build base query for filtering (without includes to avoid query complexity)
        IQueryable<GcodeFile> filterQuery = _db.GcodeFiles;

        // Apply path filter
        if (!string.IsNullOrWhiteSpace(path))
        {
            // Normalize path: remove trailing slashes, ensure leading slash
            string normalizedPath = path.TrimEnd('/');
            if (!normalizedPath.StartsWith('/'))
            {
                normalizedPath = '/' + normalizedPath;
            }

            // Join with FolderNode and filter by path
            filterQuery = filterQuery.Where(f => f.Folder != null && f.Folder.Path == normalizedPath);
        }

        // If path is null/empty, include ALL files (no filter)

        // Apply search filter at database level
        if (!string.IsNullOrWhiteSpace(search))
        {
            filterQuery = filterQuery.Where(g => g.FileName.Contains(search));
        }

        // Apply tag filtering at database level (AND logic - must have all tags)
        if (tagIds?.Length > 0)
        {
            // For each tag, ensure the gcode file has it
            foreach (Guid tagId in tagIds)
            {
                filterQuery = filterQuery.Where(g => g.Tags.Any(t => t.Id == tagId));
            }
        }

        // Apply printer model filter at database level
        if (printerModelId.HasValue)
        {
            filterQuery = filterQuery.Where(g => g.PrinterModelId == printerModelId.Value);
        }

        // Apply printerId filter at database level
        if (printerId.HasValue)
        {
            filterQuery = filterQuery.Where(g => g.SourcePrinterId == printerId.Value);
        }

        // Get total count BEFORE pagination
        int totalCount = await filterQuery.CountAsync(ct);

        // Apply sorting at database level
        IOrderedQueryable<GcodeFile> sortedQuery = (sortBy?.ToLower(), sortOrder?.ToLower()) switch
        {
            ("size", "desc") => filterQuery.OrderByDescending(g => g.FileSizeBytes),
            ("size", _) => filterQuery.OrderBy(g => g.FileSizeBytes),
            ("date", "desc") => filterQuery.OrderByDescending(g => g.UploadedAt),
            ("date", _) => filterQuery.OrderBy(g => g.UploadedAt),
            ("name", "desc") => filterQuery.OrderByDescending(g => g.FileName),
            _ => filterQuery.OrderBy(g => g.FileName) // Default: name ascending
        };

        // Apply pagination
        int skip = (page - 1) * pageSize;

        // Get the IDs and order info of files that match the filters and pagination
        var paginatedFiles = await sortedQuery
            .Skip(skip)
            .Take(pageSize)
            .Select(g => new { g.Id, g.FileName, g.UploadedAt, g.FileSizeBytes })
            .ToListAsync(ct);

        // Extract IDs in order
        var fileIds = paginatedFiles.Select(x => x.Id).ToList();

        // Now load the full files WITH includes using the IDs
        Dictionary<Guid, GcodeFile> filesByIdDict = await _db.GcodeFiles
            .AsNoTracking()
            .Where(g => fileIds.Contains(g.Id))
            .Include(g => g.Tags) // Use skip-navigation instead of TagMappings
            .Include(g => g.PrinterModel)
            .ToDictionaryAsync(g => g.Id, ct);

        // Reconstruct list in original sort order
        var files = fileIds
            .Select(id => filesByIdDict[id])
            .ToList();

        return (files, totalCount);
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
        List<string> subdirs = await _db.Set<FolderNode>()
            .Where(f => f.FolderType == "gcode" && !f.DeletedAt.HasValue && f.Path.StartsWith(normalizedParent))
            .Select(f => f.Path)
            .Distinct()
            .ToListAsync(ct);

        // Filter to only direct children (one level down)
        var directChildren = new HashSet<string>();
        foreach (string dir in subdirs)
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
                string[] segments = dir.Split(new[] { '/', Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
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
                    string[] segments = relative.Split(new[] { '/', Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length > 0)
                    {
                        directChildren.Add(segments[0]);
                    }
                }
            }
        }

        return directChildren.Distinct().OrderBy(d => d).ToList();
    }

    public async Task<List<GcodeFile>> ListValidByDirectoryAsync(string directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = "/";
        }

        // Normalize directory path: convert empty string to "/" for consistency
        if (directory.Length == 0)
        {
            directory = "/";
        }

        // Get files by finding the folder with matching path
        // For root directory ("/"), also include files with NULL folder (orphaned files)
        // Include PrinterModel for displaying printer model information in file browser
        return directory == "/"
            ? await _db.GcodeFiles
                .Include(g => g.PrinterModel)
                .Where(g => (g.Folder != null && g.Folder.Path == directory) || g.Folder == null)
                .ToListAsync(ct)
            : await _db.GcodeFiles
                .Include(g => g.PrinterModel)
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
        foreach (Guid printerId in printerIdList)
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

    public async Task<Guid?> ResolvePrinterModelIdAsync(string? extractedModelName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(extractedModelName))
        {
            return null;
        }

        try
        {
            // Step 1: Try to resolve using PrinterModelAlias (handles slicer-specific names)
            // This allows "COREONEL" (PrusaSlicer) to map to the same PrinterModel as "Prusa CORE One" (OrcaSlicer)
            // Case-insensitive comparison: slicer metadata may differ in casing from seed data
            string extractedLower = extractedModelName.ToLowerInvariant();
#pragma warning disable CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons
            Guid aliasMatch = await _db.PrinterModelAliases
                .Where(a => a.SlicerModelName != null && a.SlicerModelName.ToLower() == extractedLower)
                .Select(a => a.PrinterModelId)
                .FirstOrDefaultAsync(ct);
#pragma warning restore CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons

            if (aliasMatch != Guid.Empty)
            {
                return aliasMatch;
            }

            // Step 2: Try exact match (case-insensitive) on PrinterModel name
            PrinterModel? exactMatch = await _db.PrinterModels
                .FirstOrDefaultAsync(m => m.Name != null && m.Name.Equals(extractedModelName, StringComparison.OrdinalIgnoreCase), ct);

            if (exactMatch != null)
            {
                return exactMatch.Id;
            }

            // Step 3: Try partial/contains match (case-insensitive) if exact match fails
            // This handles cases where metadata has "Prusa CORE One" but DB has "Prusa CORE One 0.4mm"
            PrinterModel? partialMatch = await _db.PrinterModels
                .FirstOrDefaultAsync(m => m.Name != null && m.Name.Contains(extractedModelName, StringComparison.OrdinalIgnoreCase), ct);

            return partialMatch != null ? partialMatch.Id : null;
        }
        catch
        {
            return null;
        }
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
