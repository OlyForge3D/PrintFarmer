using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Queue;

/// <summary>
/// EF Core implementation of print job management repository.
/// </summary>
public class EfPrintJobManagementRepository(AppDbContext context) : IPrintJobManagementRepository
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    // ============= BASIC CRUD OPERATIONS =============
    public async Task<PrintJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.PrintJobs.FindAsync([id], ct);
    }

    public async Task<PrintJob?> GetByIdWithRelationsAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .Include(pj => pj.GcodeFile)
            .Include(pj => pj.AssignedPrinter)
                .ThenInclude(p => p!.Model)
            .FirstOrDefaultAsync(pj => pj.Id == id, ct);
    }

    public async Task<PrintJob?> GetByIdWithGcodeFileAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .Include(pj => pj.GcodeFile)
            .FirstOrDefaultAsync(pj => pj.Id == id, ct);
    }

    public async Task<PrintJob> AddAsync(PrintJob job, CancellationToken ct = default)
    {
        _ = await _context.PrintJobs.AddAsync(job, ct);
        _ = await _context.SaveChangesAsync(ct);
        return job;
    }

    public void Add(PrintJob job)
    {
        _context.PrintJobs.Add(job);
    }

    public async Task<PrintJob> UpdateAsync(PrintJob job, CancellationToken ct = default)
    {
        _ = _context.PrintJobs.Update(job);
        _ = await _context.SaveChangesAsync(ct);
        return job;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        PrintJob? job = await _context.PrintJobs.FindAsync([id], ct);
        if (job == null)
        {
            return false;
        }

        _ = _context.PrintJobs.Remove(job);
        _ = await _context.SaveChangesAsync(ct);
        return true;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _context.SaveChangesAsync(ct);

    // ============= FILTERED QUERIES =============
    public async Task<List<PrintJob>> GetFilteredJobsAsync(
        PrintJobStatus? filterStatus = null,
        string? filterModel = null,
        string? filterMaterial = null,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        IQueryable<PrintJob> query = _context.PrintJobs
            .Include(pj => pj.GcodeFile)
            .Include(pj => pj.AssignedPrinter)
                .ThenInclude(p => p!.Model)
            .AsQueryable();

        // Apply status filter or default to active jobs
        if (filterStatus.HasValue)
        {
            query = query.Where(pj => pj.Status == filterStatus.Value);
        }
        else
        {
            // Include all "active" statuses in default view:
            // - Queued: waiting in queue
            // - Assigned: assigned to printer but not yet started
            // - Starting: dispatch initiated, connecting to printer
            // - Printing: actively printing
            // - Paused: temporarily paused by user
            query = query.Where(pj =>
                pj.Status == PrintJobStatus.Queued ||
                pj.Status == PrintJobStatus.Assigned ||
                pj.Status == PrintJobStatus.Starting ||
                pj.Status == PrintJobStatus.Printing ||
                pj.Status == PrintJobStatus.Paused);
        }

        // Filter by printer model
        if (!string.IsNullOrEmpty(filterModel))
        {
            query = query.Where(pj => pj.AssignedPrinter != null &&
                pj.AssignedPrinter.Model != null &&
                pj.AssignedPrinter.Model.Name.Contains(filterModel));
        }

        // Filter by material
        if (!string.IsNullOrEmpty(filterMaterial))
        {
            query = query.Where(pj => (pj.RequiredMaterialType != null &&
                pj.RequiredMaterialType.Contains(filterMaterial)) ||
                (pj.GcodeFile != null && pj.GcodeFile.RequiredMaterial != null &&
                pj.GcodeFile.RequiredMaterial.Contains(filterMaterial)));
        }

        return await query
            .OrderByDescending(pj => pj.Priority)
            .ThenBy(pj => pj.QueuePosition)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<List<PrintJob>> GetJobsByPrinterAsync(Guid printerId, int limit = 50, CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .Where(pj => pj.AssignedPrinterId == printerId &&
                (pj.Status == PrintJobStatus.Queued || pj.Status == PrintJobStatus.Printing))
            .OrderByDescending(pj => pj.Priority)
            .ThenBy(pj => pj.QueuePosition)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<List<PrintJob>> GetJobsByStatusAsync(PrintJobStatus status, CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .Where(pj => pj.Status == status)
            .ToListAsync(ct);
    }

    public async Task<List<PrintJob>> GetJobsByStatusesAsync(IEnumerable<PrintJobStatus> statuses, CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .Where(pj => statuses.Contains(pj.Status))
            .ToListAsync(ct);
    }

    // ============= STATISTICS & ANALYTICS =============
    public async Task<(int queued, int printing, int paused, int completed, int failed)> GetQueueStatsAsync(CancellationToken ct = default)
    {
        List<PrintJob> allJobs = await _context.PrintJobs.ToListAsync(ct);

        // Count both Queued and Assigned status as "queued" for display purposes
        // Assigned = job is assigned to a printer but not yet printing
        return (
            queued: allJobs.Count(j => j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned),
            printing: allJobs.Count(j => j.Status == PrintJobStatus.Printing),
            paused: allJobs.Count(j => j.Status == PrintJobStatus.Paused),
            completed: allJobs.Count(j => j.Status == PrintJobStatus.Completed),
            failed: allJobs.Count(j => j.Status == PrintJobStatus.Failed));
    }

    public async Task<List<PrinterModelQueueStats>> GetModelStatsAsync(CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .Include(pj => pj.AssignedPrinter)
                .ThenInclude(p => p!.Model)
            .Where(pj => pj.AssignedPrinter != null && pj.AssignedPrinter.Model != null)
            .GroupBy(pj => pj.AssignedPrinter!.Model!.Name)
            .Select(g => new PrinterModelQueueStats(
                g.Key,
                g.Count(j => j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned),
                g.Count(j => j.Status == PrintJobStatus.Printing),
                g.Where(j => j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned).Min(j => (DateTime?)j.QueuedAt)))
            .ToListAsync(ct);
    }

    public async Task<(List<PrintJob> jobs, int totalCount)> GetHistoryAsync(
        int limit = 50,
        int offset = 0,
        string sortBy = "completedAt",
        CancellationToken ct = default)
    {
        IQueryable<PrintJob> query = _context.PrintJobs
            .Include(pj => pj.GcodeFile)
            .Include(pj => pj.AssignedPrinter)
                .ThenInclude(p => p!.Model)
            .Where(pj => pj.Status == PrintJobStatus.Completed || pj.Status == PrintJobStatus.Failed);

        int totalCount = await query.CountAsync(ct);

        query = sortBy.ToLowerInvariant() switch
        {
            "duration" => query.OrderByDescending(pj => pj.ActualPrintTime),
            "name" => query.OrderBy(pj => pj.GcodeFile != null ? pj.GcodeFile.FileName : string.Empty),
            "status" => query.OrderBy(pj => pj.Status),
            _ => query.OrderByDescending(pj => pj.ActualEndTime)
        };

        List<PrintJob> jobs = await query.Skip(offset).Take(limit).ToListAsync(ct);

        return (jobs, totalCount);
    }

    // ============= TIMELINE & HISTORY =============
    public async Task<List<PrintJob>> GetTimelineJobsAsync(
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        Guid? printerId = null,
        PrintJobStatus? filterStatus = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        IQueryable<PrintJob> query = _context.PrintJobs
            .Include(pj => pj.AssignedPrinter)
            .AsQueryable();

        if (dateFrom.HasValue)
        {
            query = query.Where(pj => pj.QueuedAt >= dateFrom.Value || pj.ActualStartTime >= dateFrom.Value);
        }

        if (dateTo.HasValue)
        {
            query = query.Where(pj => pj.QueuedAt <= dateTo.Value || pj.ActualEndTime <= dateTo.Value);
        }

        if (printerId.HasValue)
        {
            query = query.Where(pj => pj.AssignedPrinterId == printerId.Value);
        }

        if (filterStatus.HasValue)
        {
            query = query.Where(pj => pj.Status == filterStatus.Value);
        }

        return await query
            .OrderByDescending(pj => pj.QueuedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<PrintJob?> GetJobWithStateHistoryAsync(Guid jobId, CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .Include(pj => pj.StateHistory)
            .FirstOrDefaultAsync(pj => pj.Id == jobId, ct);
    }

    public async Task<List<PrintJob>> GetCompletedJobsForAnalyticsAsync(
        Guid? printerId = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        CancellationToken ct = default)
    {
        IQueryable<PrintJob> query = _context.PrintJobs
            .Where(pj => pj.Status == PrintJobStatus.Completed && pj.ActualEndTime.HasValue);

        if (printerId.HasValue)
        {
            query = query.Where(pj => pj.AssignedPrinterId == printerId.Value);
        }

        if (dateFrom.HasValue)
        {
            query = query.Where(pj => pj.ActualEndTime >= dateFrom.Value);
        }

        if (dateTo.HasValue)
        {
            query = query.Where(pj => pj.ActualEndTime <= dateTo.Value);
        }

        return await query.ToListAsync(ct);
    }

    // ============= RELATED ENTITIES =============
    public async Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.GcodeFiles.FindAsync([id], ct);
    }

    public async Task<Printer?> GetPrinterWithModelAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Printers
            .Include(p => p.Model)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct = default)
    {
        return await _context.Printers
            .Include(p => p.Model)
            .Include(p => p.Toolheads)
                .ThenInclude(t => t.NozzleModel)
            .Where(p => p.IsAvailable)
            .ToListAsync(ct);
    }

    public async Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct = default)
    {
        int? maxPosition = await _context.PrintJobs
            .Where(pj => pj.AssignedPrinterId == printerId && pj.Status == PrintJobStatus.Queued)
            .MaxAsync(pj => (int?)pj.QueuePosition, ct);

        return (maxPosition ?? 0) + 1;
    }

    // ============= BULK OPERATIONS =============
    public async Task<List<PrintJob>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        List<Guid> idList = ids.ToList();
        return await _context.PrintJobs
            .Where(pj => idList.Contains(pj.Id))
            .ToListAsync(ct);
    }

    public async Task UpdateManyAsync(IEnumerable<PrintJob> jobs, CancellationToken ct = default)
    {
        _context.PrintJobs.UpdateRange(jobs);
        _ = await _context.SaveChangesAsync(ct);
    }

    // ============= HISTORY SEEDING OPERATIONS =============
    public async Task<List<Printer>> GetEnabledPrintersAsync(CancellationToken ct = default)
    {
        return await _context.Printers
            .AsNoTracking()
            .Where(p => p.IsEnabled)
            .ToListAsync(ct);
    }

    public async Task UpdatePrinterLastHistorySeedAsync(Guid printerId, DateTime lastSeedUtc, CancellationToken ct = default)
    {
        Printer? printer = await _context.Printers.FindAsync([printerId], ct);
        if (printer != null)
        {
            printer.LastHistorySeedUtc = lastSeedUtc;
            _ = await _context.SaveChangesAsync(ct);
        }
    }

    public async Task<HashSet<string>> GetExternalJobIdsForPrinterAsync(Guid printerId, CancellationToken ct = default)
    {
        List<string> externalIds = await _context.PrintJobs
            .AsNoTracking()
            .Where(pj => pj.SourcePrinterId == printerId && pj.ExternalJobId != null)
            .Select(pj => pj.ExternalJobId!)
            .ToListAsync(ct);

        return externalIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<PrintJob?> GetByExternalIdAsync(Guid printerId, string externalJobId, CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .FirstOrDefaultAsync(
                pj => pj.SourcePrinterId == printerId && pj.ExternalJobId == externalJobId,
                ct);
    }

    public async Task<GcodeFile?> FindGcodeFileByFilenameAsync(string filename, CancellationToken ct = default)
    {
        string name = Path.GetFileName(filename);
        return await _context.GcodeFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Name == name || g.FileName == name, ct);
    }

    public async Task<int> GetMaxQueuePositionAsync(CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .Where(pj => pj.Status == PrintJobStatus.Queued || pj.Status == PrintJobStatus.Printing)
            .MaxAsync(pj => (int?)pj.QueuePosition, ct) ?? -1;
    }
}
