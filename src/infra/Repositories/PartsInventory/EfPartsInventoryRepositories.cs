using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.PartsInventory;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.PartsInventory;

/// <summary>EF Core implementation of the printed-part SKU repository.</summary>
public class EfPartInventoryRepository(AppDbContext context) : IPartInventoryRepository
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<List<PartInventory>> GetAllAsync(bool includeInactive, CancellationToken ct = default)
    {
        IQueryable<PartInventory> query = _context.PartInventories
            .AsNoTracking()
            .Include(p => p.DefaultBin);

        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        return await query
            .OrderBy(p => p.Sku)
            .ToListAsync(ct);
    }

    public async Task<PartInventory?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.PartInventories
            .Include(p => p.DefaultBin)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<PartInventory?> GetBySkuAsync(string sku, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return null;
        }

        string trimmed = PartInventoryIdentity.NormalizeSku(sku);
        return await _context.PartInventories
            .Include(p => p.DefaultBin)
            .FirstOrDefaultAsync(p => p.Sku == trimmed, ct);
    }

    public async Task<List<PartInventory>> GetReorderCandidatesAsync(CancellationToken ct = default)
    {
        return await _context.PartInventories
            .AsNoTracking()
            .Where(p => p.IsActive && p.OnHand <= p.ReorderPoint)
            .OrderBy(p => p.Sku)
            .ToListAsync(ct);
    }

    public async Task AddAsync(PartInventory entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await _context.PartInventories.AddAsync(entity, ct);
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return await _context.SaveChangesAsync(ct);
    }
}

/// <summary>EF Core implementation of the printed-part bin repository.</summary>
public class EfBinRepository(AppDbContext context) : IBinRepository
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<List<Bin>> GetAllAsync(bool includeInactive, CancellationToken ct = default)
    {
        IQueryable<Bin> query = _context.Bins.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(b => b.IsActive);
        }

        return await query
            .OrderBy(b => b.Code)
            .ToListAsync(ct);
    }

    public async Task<Bin?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Bins.FirstOrDefaultAsync(b => b.Id == id, ct);
    }

    public async Task<Bin?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        string trimmed = PartInventoryIdentity.NormalizeBinCode(code);
        return await _context.Bins.FirstOrDefaultAsync(b => b.Code == trimmed, ct);
    }

    public async Task AddAsync(Bin entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await _context.Bins.AddAsync(entity, ct);
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return await _context.SaveChangesAsync(ct);
    }
}

/// <summary>EF Core implementation of the printed-part adjustment ledger repository.</summary>
public class EfPartInventoryAdjustmentRepository(AppDbContext context) : IPartInventoryAdjustmentRepository
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<List<PartInventoryAdjustment>> GetForPartAsync(Guid partInventoryId, int limit, CancellationToken ct = default)
    {
        int bounded = Math.Clamp(limit, 1, 500);
        return await _context.PartInventoryAdjustments
            .AsNoTracking()
            .Include(a => a.Bin)
            .Where(a => a.PartInventoryId == partInventoryId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(bounded)
            .ToListAsync(ct);
    }

    public async Task<PartInventoryAdjustment?> GetByOperationKeyAsync(string operationKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            return null;
        }

        return await _context.PartInventoryAdjustments
            .AsNoTracking()
            .Include(a => a.Bin)
            .FirstOrDefaultAsync(a => a.OperationKey == operationKey, ct);
    }

    public async Task<List<PartInventoryAdjustment>> GetByOperationKeyAllAsync(string operationKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            return [];
        }

        return await _context.PartInventoryAdjustments
            .AsNoTracking()
            .Include(a => a.Bin)
            .Include(a => a.PartInventory)
            .Where(a => a.OperationKey == operationKey)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);
    }
}

/// <summary>EF Core implementation of the job-output mapping repository.</summary>
public class EfPartOutputMappingRepository(AppDbContext context) : IPartOutputMappingRepository
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<List<PartOutputMapping>> GetAllAsync(CancellationToken ct = default)
    {
        return await _context.PartOutputMappings
            .AsNoTracking()
            .Include(m => m.PartInventory)
            .OrderBy(m => m.PartInventory!.Sku)
            .ThenBy(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<PartOutputMapping>> GetForPartAsync(Guid partInventoryId, CancellationToken ct = default)
    {
        return await _context.PartOutputMappings
            .AsNoTracking()
            .Include(m => m.PartInventory)
            .Where(m => m.PartInventoryId == partInventoryId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<List<PartOutputMapping>> GetForGcodeFileAsync(Guid gcodeFileId, CancellationToken ct = default)
    {
        return await _context.PartOutputMappings
            .AsNoTracking()
            .Include(m => m.PartInventory)
            .Where(m => m.GcodeFileId == gcodeFileId)
            .ToListAsync(ct);
    }

    public async Task<List<PartOutputMapping>> GetForProjectFileAsync(Guid projectFileId, CancellationToken ct = default)
    {
        return await _context.PartOutputMappings
            .AsNoTracking()
            .Include(m => m.PartInventory)
            .Where(m => m.PrintProjectFileId == projectFileId)
            .ToListAsync(ct);
    }

    public async Task<PartOutputMapping?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.PartOutputMappings
            .Include(m => m.PartInventory)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<bool> SourceExistsAsync(
        Guid? gcodeFileId,
        Guid? projectFileId,
        CancellationToken ct = default)
    {
        if (gcodeFileId is Guid gcodeId)
        {
            return await _context.GcodeFiles.AsNoTracking().AnyAsync(file => file.Id == gcodeId, ct);
        }

        return projectFileId is Guid projectId
            && await _context.PrintProjectFiles.AsNoTracking().AnyAsync(file => file.Id == projectId, ct);
    }

    public async Task<bool> MappingExistsAsync(
        Guid partInventoryId,
        Guid? gcodeFileId,
        Guid? projectFileId,
        CancellationToken ct = default)
    {
        return await _context.PartOutputMappings.AsNoTracking().AnyAsync(
            mapping => mapping.PartInventoryId == partInventoryId
                && mapping.GcodeFileId == gcodeFileId
                && mapping.PrintProjectFileId == projectFileId,
            ct);
    }

    public async Task AddAsync(PartOutputMapping entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await _context.PartOutputMappings.AddAsync(entity, ct);
    }

    public Task RemoveAsync(PartOutputMapping entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        _ = _context.PartOutputMappings.Remove(entity);
        return Task.CompletedTask;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return await _context.SaveChangesAsync(ct);
    }
}
