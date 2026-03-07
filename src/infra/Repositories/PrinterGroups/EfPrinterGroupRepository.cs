using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.PrinterGroups;

/// <summary>
/// Entity Framework implementation of IPrinterGroupRepository.
/// </summary>
public class EfPrinterGroupRepository(AppDbContext db) : IPrinterGroupRepository
{
    public async Task<IReadOnlyList<PrinterGroup>> ListAllAsync(CancellationToken ct)
    {
        return await db.PrinterGroups
            .Include(g => g.Printers)
            .AsNoTracking()
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
    }

    public async Task<PrinterGroup?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return await db.PrinterGroups
            .Include(g => g.Printers)
            .FirstOrDefaultAsync(g => g.Id == id, ct);
    }

    public async Task<PrinterGroup?> GetByNameAsync(string name, CancellationToken ct)
    {
        return await db.PrinterGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => EF.Functions.Like(g.Name, name), ct);
    }

    public async Task AddAsync(PrinterGroup group, CancellationToken ct)
    {
        await db.PrinterGroups.AddAsync(group, ct);
    }

    public void Remove(PrinterGroup group)
    {
        db.PrinterGroups.Remove(group);
    }

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
    }
}
