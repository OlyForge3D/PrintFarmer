using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Entity Framework implementation of PrinterStatistics repository
/// </summary>
public class EfPrinterStatisticsRepository : IPrinterStatisticsRepository
{
    private readonly AppDbContext _context;

    public EfPrinterStatisticsRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<PrinterStatistics?> GetByPrinterIdAsync(Guid printerId, CancellationToken ct = default)
    {
        return await _context.PrinterStatisticsSet
            .FirstOrDefaultAsync(s => s.PrinterId == printerId, ct);
    }

    public async Task<List<PrinterStatistics>> GetAllAsync(CancellationToken ct = default)
    {
        return await _context.PrinterStatisticsSet
            .Include(s => s.Printer)
            .ToListAsync(ct);
    }

    public async Task UpsertAsync(PrinterStatistics statistics, CancellationToken ct = default)
    {
        PrinterStatistics? existing = await GetByPrinterIdAsync(statistics.PrinterId, ct);

        if (existing != null)
        {
            // Update existing record
            existing.TotalPrintHours = statistics.TotalPrintHours;
            existing.TotalJobsCompleted = statistics.TotalJobsCompleted;
            existing.TotalJobsFailed = statistics.TotalJobsFailed;
            existing.TotalFilamentUsedGrams = statistics.TotalFilamentUsedGrams;
            existing.TotalFilamentUsedMeters = statistics.TotalFilamentUsedMeters;
            existing.LastSyncTime = statistics.LastSyncTime;
            existing.UpdatedAt = DateTime.UtcNow;

            _context.PrinterStatisticsSet.Update(existing);
        }
        else
        {
            // Create new record
            statistics.CreatedAt = DateTime.UtcNow;
            statistics.UpdatedAt = DateTime.UtcNow;
            await _context.PrinterStatisticsSet.AddAsync(statistics, ct);
        }
    }

    public async Task DeleteByPrinterIdAsync(Guid printerId, CancellationToken ct = default)
    {
        PrinterStatistics? statistics = await GetByPrinterIdAsync(printerId, ct);
        if (statistics != null)
        {
            _context.PrinterStatisticsSet.Remove(statistics);
        }
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _context.SaveChangesAsync(ct);
    }
}
