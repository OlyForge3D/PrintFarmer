using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Printers;

public class EfPrintersRepository : IPrintersRepository
{
    private readonly AppDbContext _db;

    public EfPrintersRepository(AppDbContext db) => _db = db;

    public async Task<List<Printer>> GetAllAsync(CancellationToken ct) => await _db.Printers.AsNoTracking().ToListAsync(ct);

    public async Task<List<Printer>> GetAllWithIncludesAsync(CancellationToken ct) => await _db.Printers.AsNoTracking().Include(p => p.Manufacturer).Include(p => p.Model).Include(p => p.Location).ToListAsync(ct);

    public async Task<Printer?> FindByIdAsync(Guid id, CancellationToken ct) => await _db.Printers.FindAsync(new object?[] { id }, ct);

    public async Task<Printer?> FindByIdWithIncludesAsync(Guid id, CancellationToken ct) => await _db.Printers.Include(p => p.Manufacturer).Include(p => p.Model).FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task AddAsync(Printer p, CancellationToken ct)
    {
        _ = _db.Printers.Add(p);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Printer p, CancellationToken ct)
    {
        // Ensure we have a tracked instance of the printer to remove (in case p is untracked)
        Printer? trackedPrinter = await FindByIdAsync(p.Id, ct);
        if (trackedPrinter == null)
        {
            // Printer doesn't exist, nothing to remove
            return;
        }

        // Clean up dependent records that have NoAction delete behavior to prevent FK constraint violations

        // Remove GcodeFile records that reference this printer as source or target
        List<GcodeFile> gcodeFilesReferencing = await _db.GcodeFiles
            .Where(gf => gf.SourcePrinterId == trackedPrinter.Id || gf.TargetPrinterId == trackedPrinter.Id)
            .ToListAsync(ct);
        if (gcodeFilesReferencing.Any())
        {
            _db.GcodeFiles.RemoveRange(gcodeFilesReferencing);
        }

        // Remove PrintJob records assigned to this printer
        List<PrintJob> jobsForPrinter = await _db.PrintJobs
            .Where(j => j.AssignedPrinterId == trackedPrinter.Id)
            .ToListAsync(ct);
        if (jobsForPrinter.Any())
        {
            _db.PrintJobs.RemoveRange(jobsForPrinter);
        }

        // Remove GcodeHarvestOperation records for this printer
        List<GcodeHarvestOperation> harvestOpsForPrinter = await _db.GcodeHarvestOperations
            .Where(h => h.PrinterId == trackedPrinter.Id)
            .ToListAsync(ct);
        if (harvestOpsForPrinter.Any())
        {
            _db.GcodeHarvestOperations.RemoveRange(harvestOpsForPrinter);
        }

        // SpoolmanSpool references will be set to NULL by the database (SetNull behavior), so no need to handle them

        // Now remove the tracked printer
        _ = _db.Printers.Remove(trackedPrinter);
        _ = await _db.SaveChangesAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct) => await _db.SaveChangesAsync(ct);

    public void Detach(Printer p)
    {
        // Remove the entity from the tracker so it can be re-added without conflicts
        var entry = _db.Entry(p);
        if (entry != null && entry.State != EntityState.Detached)
        {
            entry.State = EntityState.Detached;
        }
    }

    public async Task<List<Printer>> GetPrintersForExportAsync(Guid[]? ids, CancellationToken ct)
    {
        IQueryable<Printer> q = _db.Printers
            .AsNoTracking()
            .Include(p => p.Manufacturer)
            .Include(p => p.Model)
            .Include(p => p.Location);
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

    /// <summary>
    /// Finds a printer by its IP address efficiently using a direct database query.
    /// Extracts the IP from the ServerUrl and matches against the stored IpAddress field.
    /// This is much more efficient than loading all printers into memory.
    /// </summary>
    public async Task<Printer?> FindByIpAddressAsync(string serverUrl, CancellationToken ct)
    {
        // Extract IP address from ServerUrl (format: http://ip or http://hostname)
        // Strip http/https and port (if any) to get just the host
        string inputHost = serverUrl.Replace("http://", "").Replace("https://", "").Split(':')[0];

        // Query only for the printer with matching IP - much more efficient than GetAllAsync + FirstOrDefault
        return await _db.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => !string.IsNullOrWhiteSpace(p.IpAddress) && p.IpAddress == inputHost, ct);
    }

    /// <summary>
    /// Detaches all tracked entities from the DbContext to prevent "second operation" errors
    /// in loops where multiple operations are performed on the same context.
    /// This clears the change tracker without persisting any uncommitted changes.
    /// </summary>
    public void DetachAllEntities()
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
        {
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
    }
}
