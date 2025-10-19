using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.PrinterCapabilities;

public class EfPrinterCapabilitiesRepository : IPrinterCapabilitiesRepository
{
    private readonly AppDbContext _db;

    public EfPrinterCapabilitiesRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Farm.Infrastructure.Domain.PrinterCapabilities>> GetAllWithPrinterAsync(CancellationToken ct = default)
    {
        return await _db.PrinterCapabilities
            .Include(c => c.Printer)
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<Farm.Infrastructure.Domain.PrinterCapabilities?> GetByPrinterIdAsync(Guid printerId, CancellationToken ct = default)
    {
        return await _db.PrinterCapabilities
            .Include(c => c.Printer)
            .FirstOrDefaultAsync(c => c.PrinterId == printerId, ct);
    }

    public async Task<bool> ExistsByPrinterIdAsync(Guid printerId, CancellationToken ct = default)
    {
        return await _db.PrinterCapabilities
            .AnyAsync(c => c.PrinterId == printerId, ct);
    }

    public async Task AddAsync(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, CancellationToken ct = default)
    {
        await _db.PrinterCapabilities.AddAsync(capabilities, ct);
    }

    public Task UpdateAsync(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, CancellationToken ct = default)
    {
        _db.PrinterCapabilities.Update(capabilities);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Farm.Infrastructure.Domain.PrinterCapabilities capabilities, CancellationToken ct = default)
    {
        _db.PrinterCapabilities.Remove(capabilities);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<Farm.Infrastructure.Domain.PrinterCapabilities>> GetStaleCapabilitiesAsync(
        DateTime threshold, 
        int limit, 
        CancellationToken ct = default)
    {
        return await _db.PrinterCapabilities
            .Include(c => c.Printer)
                .ThenInclude(p => p.Model)
            .Include(c => c.Printer)
                .ThenInclude(p => p.Manufacturer)
            .Where(c => c.LastUpdated < threshold && c.IsAvailable)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task LoadPrinterReferenceAsync(
        Farm.Infrastructure.Domain.PrinterCapabilities capabilities, 
        CancellationToken ct = default)
    {
        await _db.Entry(capabilities)
            .Reference(c => c.Printer)
            .LoadAsync(ct);
    }

    public async Task<Printer?> FindPrinterAsync(Guid printerId, CancellationToken ct = default)
    {
        return await _db.Printers.FindAsync(new object[] { printerId }, ct);
    }

    public async Task<Printer?> GetPrinterWithModelAndManufacturerAsync(Guid printerId, CancellationToken ct = default)
    {
        return await _db.Printers
            .Include(p => p.Model)
            .Include(p => p.Manufacturer)
            .FirstOrDefaultAsync(p => p.Id == printerId, ct);
    }

    public async Task<GcodeFile?> FindGcodeFileAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.GcodeFiles.FindAsync(new object[] { id }, ct);
    }

    public async Task<List<Farm.Infrastructure.Domain.PrinterCapabilities>> GetAvailableWithPrinterAsync(CancellationToken ct = default)
    {
        return await _db.PrinterCapabilities
            .Include(c => c.Printer)
            .Where(c => c.IsAvailable)
            .ToListAsync(ct);
    }
}
