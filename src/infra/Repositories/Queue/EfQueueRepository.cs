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
public class EfQueueRepository : IQueueRepository
{
    private readonly AppDbContext _db;

    /// <summary>
    /// Initializes a new instance of the EfQueueRepository with the provided DbContext.
    /// </summary>
    /// <param name="db">Entity Framework DbContext for database access</param>
    /// <exception cref="ArgumentNullException">Thrown when DbContext is null</exception>
    public EfQueueRepository(AppDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

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
    /// Includes related entities (Model and Toolheads) needed for job assignment decisions.
    /// </summary>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>List of available printers with model and toolhead information</returns>
    public async Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct)
    {
        return await _db.Printers
            .Include(p => p.Model)
            .Include(p => p.Toolheads)
            .Where(p => p.IsAvailable)
            .ToListAsync(ct);
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
    /// <returns>The gcode file if found; otherwise null</returns>
    public async Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct)
    {
        return await _db.GcodeFiles.FindAsync(new object[] { id }, ct).AsTask();
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
}
