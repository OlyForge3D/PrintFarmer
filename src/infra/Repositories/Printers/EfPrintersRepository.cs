using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Printers;

public class EfPrintersRepository : IPrintersRepository
{
    private readonly AppDbContext _db;

    public EfPrintersRepository(AppDbContext db) => _db = db;

    public async Task<List<Printer>> GetAllAsync(CancellationToken ct) => await _db.Printers.AsNoTracking().ToListAsync(ct);

    public async Task<List<Printer>> GetAllWithIncludesAsync(CancellationToken ct) => await _db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model).ToListAsync(ct);

    public async Task<Printer?> FindByIdAsync(Guid id, CancellationToken ct) => await _db.Printers.FindAsync(new object?[] { id }, ct);

    public async Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct) => await _db.Printers.Include(p => p.Manufacturer).Include(p => p.Model).Include(p => p.Capabilities).FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task AddAsync(Printer p, CancellationToken ct)
    {
        _ = _db.Printers.Add(p);
        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Printer p, CancellationToken ct)
    {
        // Clean up dependent records that have NoAction delete behavior to prevent FK constraint violations

        // Remove GcodeFile records that reference this printer as source or target
        var gcodeFilesReferencing = await _db.GcodeFiles
            .Where(gf => gf.SourcePrinterId == p.Id || gf.TargetPrinterId == p.Id)
            .ToListAsync(ct);
        if (gcodeFilesReferencing.Any())
        {
            _db.GcodeFiles.RemoveRange(gcodeFilesReferencing);
        }

        // Remove PrintJob records assigned to this printer
        var jobsForPrinter = await _db.PrintJobs
            .Where(j => j.AssignedPrinterId == p.Id)
            .ToListAsync(ct);
        if (jobsForPrinter.Any())
        {
            _db.PrintJobs.RemoveRange(jobsForPrinter);
        }

        // Remove GcodeHarvestOperation records for this printer
        var harvestOpsForPrinter = await _db.GcodeHarvestOperations
            .Where(h => h.PrinterId == p.Id)
            .ToListAsync(ct);
        if (harvestOpsForPrinter.Any())
        {
            _db.GcodeHarvestOperations.RemoveRange(harvestOpsForPrinter);
        }

        // SpoolmanSpool references will be set to NULL by the database (SetNull behavior), so no need to handle them

        // Now remove the printer itself
        _ = _db.Printers.Remove(p);
        await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct) => await _db.SaveChangesAsync(ct);

    public async Task<Dictionary<Guid, Domain.PrinterCapabilities>> GetCapabilitiesDictionaryAsync(Guid[]? ids, CancellationToken ct)
    {
        IQueryable<Domain.PrinterCapabilities> q = _db.PrinterCapabilities.AsNoTracking();
        if (ids != null && ids.Length > 0)
        {
            q = q.Where(c => ids.Contains(c.PrinterId));
        }

        return await q.ToDictionaryAsync(c => c.PrinterId, ct);
    }

    public async Task<List<Domain.PrinterCapabilities>> GetCapabilitiesListAsync(Guid[]? ids, CancellationToken ct)
    {
        IQueryable<Domain.PrinterCapabilities> q = _db.PrinterCapabilities.AsNoTracking();
        if (ids != null && ids.Length > 0)
        {
            q = q.Where(c => ids.Contains(c.PrinterId));
        }

        return await q.ToListAsync(ct);
    }

    public async Task<Domain.PrinterCapabilities?> GetCapabilitiesByPrinterIdAsync(Guid id, CancellationToken ct) => await _db.PrinterCapabilities.AsNoTracking().FirstOrDefaultAsync(c => c.PrinterId == id, ct);

    public async Task SaveCapabilitiesAsync(Domain.PrinterCapabilities capabilities, CancellationToken ct)
    {
        var existing = await _db.PrinterCapabilities.FirstOrDefaultAsync(c => c.PrinterId == capabilities.PrinterId, ct);
        if (existing == null)
        {
            _ = _db.PrinterCapabilities.Add(capabilities);
        }
        else
        {
            // copy updatable fields
            existing.NozzleDiameter = capabilities.NozzleDiameter;
            existing.SupportedMaterials = capabilities.SupportedMaterials;
            existing.MaxBuildVolumeX = capabilities.MaxBuildVolumeX;
            existing.MaxBuildVolumeY = capabilities.MaxBuildVolumeY;
            existing.MaxBuildVolumeZ = capabilities.MaxBuildVolumeZ;
            existing.HasHeatedBed = capabilities.HasHeatedBed;
            existing.HasEnclosure = capabilities.HasEnclosure;
            existing.MultiMaterial = capabilities.MultiMaterial;
            existing.NumberOfExtruders = capabilities.NumberOfExtruders;
            existing.MinHotendTemp = capabilities.MinHotendTemp;
            existing.MaxHotendTemp = capabilities.MaxHotendTemp;
            existing.MinBedTemp = capabilities.MinBedTemp;
            existing.MaxBedTemp = capabilities.MaxBedTemp;
            existing.CurrentMaterial = capabilities.CurrentMaterial;
            existing.CurrentSpoolId = capabilities.CurrentSpoolId;
            existing.IsAvailable = capabilities.IsAvailable;
            existing.LastUpdated = capabilities.LastUpdated;
            existing.UpdatedAt = capabilities.UpdatedAt;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct)
    {
        IQueryable<Printer> q = _db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model);
        if (ids != null && ids.Length > 0)
        {
            q = q.Where(p => ids.Contains(p.Id));
        }
        return await q.ToListAsync(ct);
    }

    public async Task<bool> ExistsByNameOrServerUrlAsync(string name, string serverUrl, CancellationToken ct)
    {
        return await _db.Printers.AnyAsync(p => p.Name == name || p.ServerUrl == serverUrl, ct);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        return await _db.Printers.CountAsync(ct);
    }

    public async Task<List<Printer>> GetByBackendAsync(PrinterBackend backend, CancellationToken ct)
    {
        return await _db.Printers
            .AsNoTracking()
            .Where(p => p.Backend == (int)backend)
            .ToListAsync(ct);
    }
}
