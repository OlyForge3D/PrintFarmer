using System.IO;
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
            .Include(pj => pj.ToolheadUsages)
            .FirstOrDefaultAsync(pj => pj.Id == id, ct);
    }

    public async Task<PrintJob?> GetByIdWithGcodeFileAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .Include(pj => pj.GcodeFile)
            .Include(pj => pj.AssignedPrinter)
                .ThenInclude(p => p!.Model)
            .Include(pj => pj.ToolheadUsages)
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

    public void Remove(PrintJob job)
    {
        _context.PrintJobs.Remove(job);
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
        DateTime? deadlineStartUtc = null,
        DateTime? deadlineEndUtc = null,
        string sortBy = "priority",
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        IQueryable<PrintJob> query = _context.PrintJobs
            .Include(pj => pj.GcodeFile)
            .Include(pj => pj.AssignedPrinter)
                .ThenInclude(p => p!.Model)
            .Include(pj => pj.ToolheadUsages)
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

        if (deadlineStartUtc.HasValue)
        {
            query = query.Where(pj => pj.DeadlineAtUtc.HasValue && pj.DeadlineAtUtc.Value >= deadlineStartUtc.Value);
        }

        if (deadlineEndUtc.HasValue)
        {
            query = query.Where(pj => pj.DeadlineAtUtc.HasValue && pj.DeadlineAtUtc.Value <= deadlineEndUtc.Value);
        }

        query = sortBy.ToLowerInvariant() switch
        {
            "deadline" => query
                .OrderBy(pj => pj.DeadlineAtUtc.HasValue ? 0 : 1)
                .ThenBy(pj => pj.DeadlineAtUtc)
                .ThenByDescending(pj => pj.Priority)
                .ThenBy(pj => pj.QueuePosition),
            "deadline_desc" => query
                .OrderBy(pj => pj.DeadlineAtUtc.HasValue ? 0 : 1)
                .ThenByDescending(pj => pj.DeadlineAtUtc)
                .ThenByDescending(pj => pj.Priority)
                .ThenBy(pj => pj.QueuePosition),
            _ => query
                .OrderByDescending(pj => pj.Priority)
                .ThenBy(pj => pj.QueuePosition)
        };

        return await query
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<List<PrintJob>> GetJobsByPrinterAsync(Guid printerId, int limit = 50, CancellationToken ct = default)
    {
        return await _context.PrintJobs
            .Include(pj => pj.GcodeFile)
            .Include(pj => pj.AssignedPrinter)
                .ThenInclude(p => p!.Model)
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

    public async Task<double> GetAverageWaitTimeMinutesAsync(Guid? printerModelId = null, int lookbackDays = 30, CancellationToken ct = default)
    {
        DateTime cutoff = DateTime.UtcNow.AddDays(-lookbackDays);

        IQueryable<PrintJob> query = _context.PrintJobs
            .Where(j => j.Status == PrintJobStatus.Completed
                     && j.ActualStartTime != null
                     && j.QueuedAt >= cutoff);

        if (printerModelId.HasValue)
        {
            query = query
                .Include(j => j.AssignedPrinter)
                .Where(j => j.AssignedPrinter != null && j.AssignedPrinter.ModelId == printerModelId.Value);
        }

        List<PrintJob> completedJobs = await query.ToListAsync(ct);

        if (completedJobs.Count == 0)
        {
            return 0;
        }

        double totalWaitMinutes = completedJobs
            .Sum(j => (j.ActualStartTime!.Value - j.QueuedAt).TotalMinutes);

        return totalWaitMinutes / completedJobs.Count;
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

    public async Task<(List<PrintJob> jobs, int totalCount, int completedCount, int failedCount, int cancelledCount, long totalPrintTimeSeconds)> GetHistoryAsync(
        int limit = 50,
        int offset = 0,
        string sortBy = "completedAt",
        List<string>? statuses = null,
        DateTime? dateStart = null,
        DateTime? dateEnd = null,
        DateTime? deadlineStartUtc = null,
        DateTime? deadlineEndUtc = null,
        CancellationToken ct = default)
    {
        IQueryable<PrintJob> query = _context.PrintJobs
            .Include(pj => pj.GcodeFile)
            .Include(pj => pj.AssignedPrinter)
                .ThenInclude(p => p!.Model)
            .Include(pj => pj.ToolheadUsages)
            .Include(pj => pj.Tags);

        // Filter by statuses - default to completed/failed/cancelled if not specified
        if (statuses != null && statuses.Count > 0)
        {
            List<PrintJobStatus> statusEnums = statuses
                .Select(s => Enum.TryParse<PrintJobStatus>(s, ignoreCase: true, out var status) ? status : (PrintJobStatus?)null)
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .ToList();

            if (statusEnums.Count > 0)
            {
                query = query.Where(pj => statusEnums.Contains(pj.Status));
            }
        }
        else
        {
            // Default: show completed, failed, and cancelled jobs
            query = query.Where(pj => pj.Status == PrintJobStatus.Completed ||
                                      pj.Status == PrintJobStatus.Failed ||
                                      pj.Status == PrintJobStatus.Cancelled);
        }

        // Filter by date range (use ActualEndTime for completed/failed, QueuedAt for cancelled)
        if (dateStart.HasValue)
        {
            query = query.Where(pj => (pj.ActualEndTime ?? pj.QueuedAt) >= dateStart.Value);
        }

        if (dateEnd.HasValue)
        {
            query = query.Where(pj => (pj.ActualEndTime ?? pj.QueuedAt) <= dateEnd.Value);
        }

        if (deadlineStartUtc.HasValue)
        {
            query = query.Where(pj => pj.DeadlineAtUtc.HasValue && pj.DeadlineAtUtc.Value >= deadlineStartUtc.Value);
        }

        if (deadlineEndUtc.HasValue)
        {
            query = query.Where(pj => pj.DeadlineAtUtc.HasValue && pj.DeadlineAtUtc.Value <= deadlineEndUtc.Value);
        }

        // Calculate statistics for the entire filtered result set (before pagination)
        int totalCount = await query.CountAsync(ct);
        int completedCount = await query.CountAsync(pj => pj.Status == PrintJobStatus.Completed, ct);
        int failedCount = await query.CountAsync(pj => pj.Status == PrintJobStatus.Failed, ct);
        int cancelledCount = await query.CountAsync(pj => pj.Status == PrintJobStatus.Cancelled, ct);

        // Sum total print time (in seconds) for jobs with ActualPrintTime
        // Note: We fetch the values and sum client-side to avoid EF Core translation issues with TimeSpan
        List<TimeSpan> printTimes = await query
            .Where(pj => pj.ActualPrintTime.HasValue)
            .Select(pj => pj.ActualPrintTime!.Value)
            .ToListAsync(ct);
        long totalPrintTimeSeconds = printTimes.Sum(t => (long)t.TotalSeconds);

        query = sortBy.ToLowerInvariant() switch
        {
            "duration" => query.OrderByDescending(pj => pj.ActualPrintTime),
            "name" => query.OrderBy(pj => pj.GcodeFile != null ? pj.GcodeFile.Name : pj.Name),
            "status" => query.OrderBy(pj => pj.Status),
            "oldest" => query.OrderBy(pj => pj.ActualEndTime ?? pj.QueuedAt),
            "deadline" => query.OrderBy(pj => pj.DeadlineAtUtc.HasValue ? 0 : 1).ThenBy(pj => pj.DeadlineAtUtc),
            "deadline_desc" => query.OrderBy(pj => pj.DeadlineAtUtc.HasValue ? 0 : 1).ThenByDescending(pj => pj.DeadlineAtUtc),
            _ => query.OrderByDescending(pj => pj.ActualEndTime ?? pj.QueuedAt)
        };

        List<PrintJob> jobs = await query.Skip(offset).Take(limit).ToListAsync(ct);

        return (jobs, totalCount, completedCount, failedCount, cancelledCount, totalPrintTimeSeconds);
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
            .Include(pj => pj.GcodeFile)
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
            .Include(pj => pj.GcodeFile)
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
        // Use ExecuteUpdateAsync on PrinterServiceState instead of Printer to avoid
        // bumping Printer.RowVersion which could conflict with concurrent user edits.
        await _context.PrinterServiceStates
            .Where(s => s.PrinterId == printerId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastHistorySeedUtc, lastSeedUtc), ct);
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

    public async Task<PrintJob?> FindExistingJobForHistoryMatchAsync(
        Guid printerId,
        string filename,
        DateTime startTimeUtc,
        DateTime? endTimeUtc,
        CancellationToken ct = default)
    {
        string fileNameOnly = Path.GetFileName(filename);
        string fileStem = Path.GetFileNameWithoutExtension(filename);

        // Allow some clock drift and differences in how providers report start times.
        DateTime startMinUtc = startTimeUtc.AddMinutes(-15);
        DateTime startMaxUtc = startTimeUtc.AddMinutes(15);

        IQueryable<PrintJob> query = _context.PrintJobs
            .Include(pj => pj.GcodeFile)
            .Where(pj => pj.AssignedPrinterId == printerId)

            // If ExternalJobId is already set, it will be deduped by GetExternalJobIdsForPrinterAsync.
            // We only want to match jobs that were created by PrintFarmer but not yet linked to a printer history ID.
            .Where(pj => pj.ExternalJobId == null && pj.SourcePrinterId == null)
            .Where(pj => !pj.WasSeededFromHistory)
            .Where(pj => (pj.ActualStartTime ?? pj.QueuedAt) >= startMinUtc && (pj.ActualStartTime ?? pj.QueuedAt) <= startMaxUtc)
            .Where(pj =>
                pj.Name == fileNameOnly ||
                pj.Name == fileStem ||
                (pj.GcodeFile != null && (pj.GcodeFile.Name == fileNameOnly || pj.GcodeFile.FileName == fileNameOnly)));

        // If we have an end time, prefer a tighter match.
        if (endTimeUtc.HasValue)
        {
            DateTime endMinUtc = endTimeUtc.Value.AddMinutes(-15);
            DateTime endMaxUtc = endTimeUtc.Value.AddMinutes(15);
            query = query.Where(pj => (pj.ActualEndTime ?? pj.UpdatedAt) >= endMinUtc && (pj.ActualEndTime ?? pj.UpdatedAt) <= endMaxUtc);
        }

        // Pick the most recently-started candidate within the match window.
        // (We avoid provider-specific date diff functions to keep this working across SQLite/PostgreSQL/MySQL/SQL Server.)
        return await query
            .OrderByDescending(pj => pj.ActualStartTime ?? pj.QueuedAt)
            .FirstOrDefaultAsync(ct);
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

    public async Task<List<Toolhead>> GetToolheadsForPrinterAsync(Guid printerId, CancellationToken ct = default)
    {
        return await _context.Toolheads
            .Where(t => t.PrinterId == printerId)
            .OrderBy(t => t.Index)
            .ToListAsync(ct);
    }

    public Task AddToolheadUsageAsync(PrintJobToolheadUsage usage, CancellationToken ct = default)
    {
        _context.Set<PrintJobToolheadUsage>().Add(usage);
        return Task.CompletedTask;
    }
}
