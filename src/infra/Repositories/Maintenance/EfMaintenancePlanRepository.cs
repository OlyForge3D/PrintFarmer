using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// EF Core implementation of maintenance plan repository.
/// </summary>
public class EfMaintenancePlanRepository(AppDbContext context) : IMaintenancePlanRepository
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<List<MaintenancePlan>> GetAllAsync(bool? activeOnly = null, CancellationToken ct = default)
    {
        IQueryable<MaintenancePlan> query = _context.MaintenancePlans
            .AsNoTracking()
            .Include(p => p.PrinterModel)
            .Include(p => p.Manufacturer)
            .Include(p => p.Tasks.OrderBy(t => t.SortOrder))
                .ThenInclude(t => t.TaskComponents)
                    .ThenInclude(tc => tc.MaintenanceComponent);

        if (activeOnly == true)
        {
            query = query.Where(p => p.IsActive);
        }

        return await query
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task<MaintenancePlan?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.MaintenancePlans
            .Include(p => p.PrinterModel)
            .Include(p => p.Manufacturer)
            .Include(p => p.Tasks.OrderBy(t => t.SortOrder))
                .ThenInclude(t => t.TaskComponents)
                    .ThenInclude(tc => tc.MaintenanceComponent)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<List<MaintenancePlan>> GetPlansForPrinterAsync(Guid printerId, CancellationToken ct = default)
    {
        Printer? printer = await _context.Printers
            .AsNoTracking()
            .Include(p => p.Model)
            .FirstOrDefaultAsync(p => p.Id == printerId, ct);

        if (printer == null)
        {
            return [];
        }

        Guid? modelId = printer.Model != null ? printer.ModelId : null;
        Guid? manufacturerId = printer.Model?.ManufacturerId;
        int? motionType = printer.Model?.MotionType;

        return await _context.MaintenancePlans
            .AsNoTracking()
            .Include(p => p.Tasks.OrderBy(t => t.SortOrder))
            .Where(p => p.IsActive &&
                (p.PrinterId == printerId ||
                 (modelId != null && p.PrinterModelId == modelId) ||
                 (manufacturerId != null && p.ManufacturerId == manufacturerId) ||
                 (motionType != null && p.MotionType == motionType) ||
                 (p.PrinterId == null && p.PrinterModelId == null && p.ManufacturerId == null && p.MotionType == null)))
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }

    public async Task AddAsync(MaintenancePlan plan, CancellationToken ct = default)
    {
        await _context.MaintenancePlans.AddAsync(plan, ct);
        await _context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(MaintenancePlan plan, CancellationToken ct = default)
    {
        // Entity is already tracked from GetByIdAsync — rely on change tracking
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(MaintenancePlan plan, CancellationToken ct = default)
    {
        _context.MaintenancePlans.Remove(plan);
        await _context.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
