using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Harvest;

public class EfHarvestRepository(AppDbContext db) : IHarvestRepository
{
    private readonly AppDbContext _db = db;

    // GcodeHarvestOperation operations
    public async Task<GcodeHarvestOperation?> GetOperationByIdAsync(Guid operationId, CancellationToken ct = default)
    {
        return await _db.GcodeHarvestOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == operationId, ct);
    }

    // Gets operation with tracking enabled for modifications (e.g., cancellation)
    public async Task<GcodeHarvestOperation?> GetOperationByIdTrackedAsync(Guid operationId, CancellationToken ct = default)
    {
        return await _db.GcodeHarvestOperations
            .FirstOrDefaultAsync(o => o.Id == operationId, ct);
    }

    public async Task<GcodeHarvestOperation?> GetOperationWithPrinterAsync(Guid operationId, CancellationToken ct = default)
    {
        return await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .FirstOrDefaultAsync(h => h.Id == operationId, ct);
    }

    public async Task<GcodeHarvestOperation?> GetActiveOperationForPrinterAsync(Guid printerId, CancellationToken ct = default)
    {
        return await _db.GcodeHarvestOperations
            .FirstOrDefaultAsync(h => h.PrinterId == printerId && h.Status == GcodeHarvestStatus.Running, ct);
    }

    public async Task<List<GcodeHarvestOperation>> GetOperationsAsync(Guid? printerId, GcodeHarvestStatus? status, int limit, int offset, CancellationToken ct = default)
    {
        IQueryable<GcodeHarvestOperation> query = _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .AsQueryable();

        if (printerId.HasValue)
        {
            query = query.Where(h => h.PrinterId == printerId.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(h => h.Status == status.Value);
        }

        return await query
            .OrderByDescending(h => h.StartedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<List<GcodeHarvestOperation>> GetRecentOperationsForPrinterAsync(Guid printerId, int count, CancellationToken ct = default)
    {
        return await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .Where(h => h.PrinterId == printerId)
            .OrderByDescending(h => h.StartedAt)
            .Take(count)
            .ToListAsync(ct);
    }

    public async Task<List<GcodeHarvestOperation>> GetActiveOperationsAsync(CancellationToken ct = default)
    {
        return await _db.GcodeHarvestOperations
            .Include(h => h.Printer)
            .Where(h => h.Status == GcodeHarvestStatus.Running)
            .OrderByDescending(h => h.StartedAt)
            .ToListAsync(ct);
    }

    public async Task<List<GcodeHarvestOperation>> GetRunningOperationsWithFilesFoundAsync(CancellationToken ct = default)
    {
        return await _db.GcodeHarvestOperations
            .Where(o => o.Status == GcodeHarvestStatus.Running && o.FilesFound > 0)
            .ToListAsync(ct);
    }

    public Task AddOperationAsync(GcodeHarvestOperation operation, CancellationToken ct = default)
    {
        _ = _db.GcodeHarvestOperations.Add(operation);
        return Task.CompletedTask;
    }

    public Task UpdateOperationAsync(GcodeHarvestOperation operation, CancellationToken ct = default)
    {
        _ = _db.GcodeHarvestOperations.Update(operation);
        return Task.CompletedTask;
    }

    // HarvestDiscoveredFile operations
    public async Task<HarvestDiscoveredFile?> GetDiscoveredFileByIdAsync(Guid fileId, Guid operationId, CancellationToken ct = default)
    {
        return await _db.HarvestDiscoveredFiles
            .FirstOrDefaultAsync(f => f.Id == fileId && f.HarvestOperationId == operationId, ct);
    }

    public async Task<List<HarvestDiscoveredFile>> GetDiscoveredFilesAsync(Guid operationId, CancellationToken ct = default)
    {
        return await _db.HarvestDiscoveredFiles
            .Where(d => d.HarvestOperationId == operationId)
            .OrderBy(d => d.FileName)
            .ToListAsync(ct);
    }

    public async Task<HarvestDiscoveredFile[]> GetDiscoveredFilesByIdsAsync(List<Guid> fileIds, CancellationToken ct = default)
    {
        return await _db.HarvestDiscoveredFiles
            .Where(d => fileIds.Contains(d.Id))
            .ToArrayAsync(ct);
    }

    public async Task<int> GetDiscoveredFilesCountAsync(Guid operationId, CancellationToken ct = default)
    {
        return await _db.HarvestDiscoveredFiles
            .Where(d => d.HarvestOperationId == operationId)
            .CountAsync(ct);
    }

    public async Task<int> GetDiscoveredFilesCountWithSearchAsync(Guid operationId, string search, CancellationToken ct = default)
    {
        // Load all files with the operation ID first (required for SQLite compatibility with case-insensitive Contains)
        List<HarvestDiscoveredFile> allFiles = await _db.HarvestDiscoveredFiles
            .Where(d => d.HarvestOperationId == operationId)
            .ToListAsync(ct);

        // Apply client-side filtering for case-insensitive search
        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim().ToLowerInvariant();
            return allFiles.Count(d => d.FileName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return allFiles.Count;
    }

    public async Task<List<HarvestDiscoveredFile>> GetDiscoveredFilesPagedAsync(Guid operationId, int page, int pageSize, string? search, CancellationToken ct = default)
    {
        // Load all files with the operation ID first (required for SQLite compatibility with case-insensitive Contains)
        List<HarvestDiscoveredFile> allFiles = await _db.HarvestDiscoveredFiles
            .Where(d => d.HarvestOperationId == operationId)
            .ToListAsync(ct);

        // Apply client-side filtering for case-insensitive search
        IEnumerable<HarvestDiscoveredFile> query = allFiles.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim().ToLowerInvariant();
            query = query.Where(d => d.FileName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderBy(d => d.FileName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    public Task AddDiscoveredFileAsync(HarvestDiscoveredFile file, CancellationToken ct = default)
    {
        _ = _db.HarvestDiscoveredFiles.Add(file);
        return Task.CompletedTask;
    }

    public async Task<bool> DiscoveredFileExistsByNameAsync(Guid operationId, string fileName, CancellationToken ct = default)
    {
        return await _db.HarvestDiscoveredFiles
            .AnyAsync(d => d.HarvestOperationId == operationId && d.FileName == fileName, ct);
    }

    public async Task<HarvestDiscoveredFile?> GetDiscoveredFileByOperationAndFileNameAsync(Guid operationId, string fileName, CancellationToken ct = default)
    {
        return await _db.HarvestDiscoveredFiles
            .FirstOrDefaultAsync(d => d.HarvestOperationId == operationId && d.FileName == fileName, ct);
    }

    public Task UpdateDiscoveredFileAsync(HarvestDiscoveredFile file, CancellationToken ct = default)
    {
        _ = _db.HarvestDiscoveredFiles.Update(file);
        return Task.CompletedTask;
    }

    public Task DeleteDiscoveredFileAsync(HarvestDiscoveredFile file, CancellationToken ct = default)
    {
        _ = _db.HarvestDiscoveredFiles.Remove(file);
        return Task.CompletedTask;
    }

    public async Task DeleteDiscoveredFilesByOperationAsync(Guid operationId, CancellationToken ct = default)
    {
        // Get all files for this operation
        List<HarvestDiscoveredFile> files = await _db.HarvestDiscoveredFiles
            .Where(d => d.HarvestOperationId == operationId)
            .ToListAsync(ct);

        // Remove all of them
        _db.HarvestDiscoveredFiles.RemoveRange(files);
    }

    // Harvest file mapping operations
    public async Task CreateFileImportMappingAsync(HarvestDiscoveredFile discoveredFile, GcodeFile gcodeFile, CancellationToken ct = default)
    {
        HarvestFileGcodeFileMapping mapping = new()
        {
            Id = Guid.NewGuid(),
            HarvestDiscoveredFile = discoveredFile,
            HarvestDiscoveredFileId = discoveredFile.Id,
            GcodeFile = gcodeFile,
            GcodeFileId = gcodeFile.Id,
            CreatedAt = DateTime.UtcNow
        };

        _ = await _db.HarvestFileGcodeFileMappings.AddAsync(mapping, ct);

        // Do NOT save here - let the caller save when all changes are ready
        // This prevents transaction issues with concurrent imports
    }

    // Combined operations
    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        _ = await _db.SaveChangesAsync(ct);
    }
}
