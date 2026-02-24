using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Repositories.Queue;

/// <summary>
/// Entity Framework Core implementation of IQueueRepository.
/// Provides both basic CRUD operations and specialized queries for print job queue management.
/// </summary>
/// <remarks>
/// This repository implements the full contract of IQueueRepository using Entity Framework Core
/// to interact with the PrintJob, Printer, GcodeFile, and related database tables.
///
/// Key implementation details:
/// - Basic CRUD operations (GetAllAsync, FindByIdAsync, AddAsync, RemoveAsync) for individual job management
/// - Specialized query methods for queue operations with optimized includes and filtering
/// - Proper entity relationship loading using Include() to avoid N+1 query problems
/// - Ordering by job status (printing/starting prioritized), then priority, then queue time
/// - AsNoTracking() for read-only queries to improve performance and reduce change tracking overhead
/// - Supports atomic transactions through shared DbContext from Unit of Work pattern
/// - Efficient aggregation queries for position tracking and job counting
///
/// All methods are optimized for the specific query patterns needed by JobQueueService,
/// QueueDataService, and other queue-related operations. Methods include proper error handling
/// and support for cancellation tokens throughout for responsive async operations.
/// </remarks>
/// <remarks>
/// Initializes a new instance of the EfQueueRepository with the provided DbContext.
/// </remarks>
/// <param name="db">Entity Framework DbContext for database access</param>
/// <exception cref="ArgumentNullException">Thrown when DbContext is null</exception>
public class EfQueueRepository(AppDbContext db) : IQueueRepository
{
    private readonly AppDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// Retrieves all print jobs from the database.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of all print jobs in the database</returns>
    public async Task<List<PrintJob>> GetAllAsync(CancellationToken ct) => await _db.PrintJobs.AsNoTracking().ToListAsync(ct);

    /// <summary>
    /// Finds a print job by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier of the print job to find</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The print job if found; otherwise null</returns>
    public async Task<PrintJob?> FindByIdAsync(Guid id, CancellationToken ct) => await _db.PrintJobs.FindAsync(new object?[] { id }, ct);

    /// <summary>
    /// Adds a new print job to the database and saves changes immediately.
    /// </summary>
    /// <param name="item">The print job to add</param>
    /// <param name="ct">Cancellation token for async operation</param>
    public async Task AddAsync(PrintJob item, CancellationToken ct)
    {
        _ = _db.PrintJobs.Add(item);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes a print job from the database and saves changes immediately.
    /// </summary>
    /// <param name="item">The print job to remove</param>
    /// <param name="ct">Cancellation token for async operation</param>
    public async Task RemoveAsync(PrintJob item, CancellationToken ct)
    {
        _ = _db.PrintJobs.Remove(item);
        _ = await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Persists all pending changes to the database in a single transaction.
    /// Used when coordinating changes across multiple repositories through Unit of Work.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    public async Task SaveChangesAsync(CancellationToken ct) => await _db.SaveChangesAsync(ct);

    // Specialized queue query methods

    /// <summary>
    /// Retrieves all printers that are available for print job assignment.
    /// Includes related entities (Model with Aliases, and Toolheads) needed for job assignment decisions.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of available printers with model and toolhead information</returns>
    public async Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct)
    {
        return await _db.Printers
            .Include(p => p.Model)
                .ThenInclude(m => m!.Aliases)
            .Include(p => p.Toolheads)
                .ThenInclude(t => t.NozzleModel)
            .Where(p => p.IsAvailable)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Retrieves all printers that are available and compatible with the specified model name or alias.
    /// Matches against both canonical model names (case-insensitive, normalizing whitespace/dashes)
    /// and slicer-specific aliases (e.g., "COREONEL" matches "Prusa CORE One L").
    /// </summary>
    /// <param name="modelNameOrAlias">The printer model name or slicer alias to match</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of compatible printers with model and toolhead information</returns>
    public async Task<List<Printer>> GetCompatiblePrintersAsync(string modelNameOrAlias, CancellationToken ct)
    {
        // Normalize the search term for comparison
        string normalizedSearch = NormalizeModelName(modelNameOrAlias);

        // Get all available printers with their models, aliases, and toolhead details (including nozzle)
        List<Printer> allPrinters = await _db.Printers
            .Include(p => p.Model)
                .ThenInclude(m => m!.Aliases)
            .Include(p => p.Toolheads)
                .ThenInclude(t => t.NozzleModel)
            .Where(p => p.IsAvailable)
            .ToListAsync(ct);

        // Filter in-memory for flexible matching (EF Core can't translate complex string normalization)
        return allPrinters.Where(p =>
        {
            if (p.Model == null)
            {
                return false;
            }

            // Check canonical model name
            string normalizedModelName = NormalizeModelName(p.Model.Name ?? string.Empty);
            if (IsModelMatch(normalizedSearch, normalizedModelName))
            {
                return true;
            }

            // Check aliases
            if (p.Model.Aliases != null)
            {
                foreach (var alias in p.Model.Aliases)
                {
                    string normalizedAlias = NormalizeModelName(alias.SlicerModelName ?? string.Empty);
                    if (IsModelMatch(normalizedSearch, normalizedAlias))
                    {
                        return true;
                    }
                }
            }

            return false;
        }).ToList();
    }

    /// <summary>
    /// Normalizes a model name for comparison by converting to lowercase and replacing separators with spaces.
    /// </summary>
    private static string NormalizeModelName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return name.ToLowerInvariant().Replace('-', ' ').Replace('_', ' ').Trim();
    }

    /// <summary>
    /// Checks if two normalized model names match (exact or substring match).
    /// </summary>
    private static bool IsModelMatch(string search, string candidate)
    {
        if (string.IsNullOrEmpty(search) || string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        return search == candidate || search.Contains(candidate) || candidate.Contains(search);
    }

    /// <summary>
    /// Retrieves all print jobs assigned to a specific printer, ordered by priority and queue time.
    /// Jobs actively printing or starting are prioritized, followed by queued jobs ordered by priority
    /// and the time they were queued.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Ordered list of print jobs for the specified printer</returns>
    public async Task<List<PrintJob>> GetPrintJobsForPrinterAsync(Guid printerId, CancellationToken ct)
    {
        return await _db.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .Where(j => j.AssignedPrinterId == printerId)
            .OrderBy(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting ? 0 : 1)
            .ThenBy(j => j.Priority)
            .ThenBy(j => j.QueuedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Retrieves the currently executing or starting print job for a specific printer.
    /// Returns null if the printer has no active job.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The current print job if one exists; otherwise null</returns>
    public async Task<PrintJob?> GetCurrentJobForPrinterAsync(Guid printerId, CancellationToken ct)
    {
        return await _db.PrintJobs
            .FirstOrDefaultAsync(j => j.AssignedPrinterId == printerId && (j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Printing), ct);
    }

    /// <summary>
    /// Retrieves a gcode file by its unique identifier.
    /// </summary>
    /// <param name="id">The unique identifier of the gcode file</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The gcode file with PrinterModel loaded if found; otherwise null</returns>
    public async Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct)
    {
        // Include PrinterModel so we can use it for auto-assign when queueing jobs
        return await _db.GcodeFiles
            .Include(f => f.PrinterModel)
            .FirstOrDefaultAsync(f => f.Id == id, ct);
    }

    /// <summary>
    /// Retrieves a print job by its unique identifier with all related entities loaded.
    /// Includes GcodeFile and AssignedPrinter information for complete job context.
    /// </summary>
    /// <param name="id">The unique identifier of the print job</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The print job with all related entities if found; otherwise null</returns>
    public async Task<PrintJob?> GetPrintJobByIdAsync(Guid id, CancellationToken ct)
    {
        return await _db.PrintJobs.Include(j => j.GcodeFile).Include(j => j.AssignedPrinter).FirstOrDefaultAsync(j => j.Id == id, ct);
    }

    /// <summary>
    /// Counts the number of queued or assigned print jobs for a specific printer.
    /// This count excludes jobs currently printing or starting.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The count of queued or assigned jobs for the printer</returns>
    public async Task<int> CountQueuedJobsForPrinterAsync(Guid printerId, CancellationToken ct)
    {
        return await _db.PrintJobs.CountAsync(j => j.AssignedPrinterId == printerId && (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned), ct);
    }

    /// <summary>
    /// Determines the next available queue position for a print job on a specific printer.
    /// The position is one higher than the current maximum queue position for that printer.
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The next available queue position (1-based)</returns>
    public async Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct)
    {
        int? max = await _db.PrintJobs.Where(j => j.AssignedPrinterId == printerId && (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned)).MaxAsync(j => (int?)j.QueuePosition, ct);
        return (max ?? 0) + 1;
    }

    /// <summary>
    /// Determines the next available queue position for unassigned print jobs in the global queue.
    /// The position is one higher than the current maximum queue position globally.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The next available global queue position (1-based)</returns>
    public async Task<int> GetNextGlobalQueuePositionAsync(CancellationToken ct)
    {
        int? max = await _db.PrintJobs.Where(j => j.Status == PrintJobStatus.Queued).MaxAsync(j => (int?)j.QueuePosition, ct);
        return (max ?? 0) + 1;
    }

    /// <summary>
    /// Counts the number of active print jobs (queued, assigned, starting, or printing) that use a specific gcode file.
    /// Useful for determining if a gcode file is in use before deletion.
    /// </summary>
    /// <param name="gcodeFileId">The unique identifier of the gcode file</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>The count of active jobs using the specified gcode file</returns>
    public async Task<int> CountActiveJobsUsingGcodeAsync(Guid gcodeFileId, CancellationToken ct)
    {
        return await _db.PrintJobs
            .Where(j => j.GcodeFileId == gcodeFileId &&
                       (j.Status == PrintJobStatus.Queued ||
                        j.Status == PrintJobStatus.Assigned ||
                        j.Status == PrintJobStatus.Starting ||
                        j.Status == PrintJobStatus.Printing))
            .CountAsync(ct);
    }

    public async Task ClearGcodeFileReferencesAsync(Guid gcodeFileId, CancellationToken ct)
    {
        List<PrintJob> jobs = await _db.PrintJobs
            .Where(j => j.GcodeFileId == gcodeFileId)
            .ToListAsync(ct);

        foreach (PrintJob job in jobs)
        {
            job.GcodeFileId = null;
        }
    }

    /// <summary>
    /// Retrieves all print jobs assigned to any of the specified printers in a single query.
    /// Results are ordered by status priority (printing/starting first), then priority, then queue time.
    /// </summary>
    public async Task<List<PrintJob>> GetPrintJobsForPrintersAsync(IEnumerable<Guid> printerIds, CancellationToken ct)
    {
        List<Guid> ids = printerIds.ToList();
        return await _db.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .Where(j => j.AssignedPrinterId != null && ids.Contains(j.AssignedPrinterId.Value))
            .OrderBy(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting ? 0 : 1)
            .ThenBy(j => j.Priority)
            .ThenBy(j => j.QueuedAt)
            .ToListAsync(ct);
    }
}
