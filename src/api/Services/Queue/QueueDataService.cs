using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Queue
{
    /// <summary>
    /// Service that provides domain-specific query methods for print job queue management.
    /// Wraps the basic IQueueRepository with specialized queries for queue operations.
    /// </summary>
    public interface IQueueDataService
    {
        /// <summary>
        /// Get all printers that are available for print job assignment.
        /// </summary>
        Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct);

        /// <summary>
        /// Get all print jobs assigned to a specific printer, ordered by priority and queue time.
        /// </summary>
        Task<List<PrintJob>> GetPrintJobsForPrinterAsync(Guid printerId, CancellationToken ct);

        /// <summary>
        /// Get the currently printing or starting job for a printer.
        /// </summary>
        Task<PrintJob?> GetCurrentJobForPrinterAsync(Guid printerId, CancellationToken ct);

        /// <summary>
        /// Get a gcode file by ID.
        /// </summary>
        Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Get a print job by ID with all related entities.
        /// </summary>
        Task<PrintJob?> GetPrintJobByIdAsync(Guid id, CancellationToken ct);

        /// <summary>
        /// Count queued or assigned jobs for a specific printer.
        /// </summary>
        Task<int> CountQueuedJobsForPrinterAsync(Guid printerId, CancellationToken ct);

        /// <summary>
        /// Get the next queue position for a printer's queue.
        /// </summary>
        Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct);

        /// <summary>
        /// Get all print jobs in the queue with all related entities.
        /// </summary>
        Task<List<PrintJob>> GetAllPrintJobsAsync(CancellationToken ct);

        /// <summary>
        /// Get the next global queue position for unassigned jobs.
        /// </summary>
        Task<int> GetNextGlobalQueuePositionAsync(CancellationToken ct);

        /// <summary>
        /// Count active jobs (queued, assigned, starting, or printing) using a specific gcode file.
        /// </summary>
        Task<int> CountActiveJobsUsingGcodeAsync(Guid gcodeFileId, CancellationToken ct);
    }

    /// <summary>
    /// Implementation of IQueueDataService using IDbContextFactory for specialized queries.
    /// Creates a new DbContext instance for each operation to avoid threading issues.
    /// </summary>
    public class QueueDataService : IQueueDataService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;

        public QueueDataService(IDbContextFactory<AppDbContext> dbFactory)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        }

        public async Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.Printers
                .Include(p => p.Model)
                .Include(p => p.Toolheads)
                .Where(p => p.IsAvailable)
                .ToListAsync(ct);
        }

        public async Task<List<PrintJob>> GetPrintJobsForPrinterAsync(Guid printerId, CancellationToken ct)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.PrintJobs
                .Include(j => j.GcodeFile)
                .Include(j => j.AssignedPrinter)
                .Where(j => j.AssignedPrinterId == printerId)
                .OrderBy(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting ? 0 : 1)
                .ThenBy(j => j.Priority)
                .ThenBy(j => j.QueuedAt)
                .ToListAsync(ct);
        }

        public async Task<List<PrintJob>> GetAllPrintJobsAsync(CancellationToken ct)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.PrintJobs
                .Include(j => j.GcodeFile)
                .Include(j => j.AssignedPrinter)
                .OrderBy(j => j.QueuePosition)
                .ThenBy(j => j.CreatedAt)
                .ToListAsync(ct);
        }

        public async Task<PrintJob?> GetCurrentJobForPrinterAsync(Guid printerId, CancellationToken ct)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.PrintJobs
                .FirstOrDefaultAsync(j => j.AssignedPrinterId == printerId && (j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Printing), ct);
        }

        public async Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.GcodeFiles.FindAsync(new object[] { id }, ct).AsTask();
        }

        public async Task<PrintJob?> GetPrintJobByIdAsync(Guid id, CancellationToken ct)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.PrintJobs.Include(j => j.GcodeFile).Include(j => j.AssignedPrinter).FirstOrDefaultAsync(j => j.Id == id, ct);
        }

        public async Task<int> CountQueuedJobsForPrinterAsync(Guid printerId, CancellationToken ct)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.PrintJobs.CountAsync(j => j.AssignedPrinterId == printerId && (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned), ct);
        }

        public async Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct)
        {
            using var db = _dbFactory.CreateDbContext();
            int? max = await db.PrintJobs.Where(j => j.AssignedPrinterId == printerId && (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned)).MaxAsync(j => (int?)j.QueuePosition, ct);
            return (max ?? 0) + 1;
        }

        public async Task<int> GetNextGlobalQueuePositionAsync(CancellationToken ct)
        {
            using var db = _dbFactory.CreateDbContext();
            int? max = await db.PrintJobs.Where(j => j.Status == PrintJobStatus.Queued).MaxAsync(j => (int?)j.QueuePosition, ct);
            return (max ?? 0) + 1;
        }

        public async Task<int> CountActiveJobsUsingGcodeAsync(Guid gcodeFileId, CancellationToken ct)
        {
            using var db = _dbFactory.CreateDbContext();
            return await db.PrintJobs
                .Where(j => j.GcodeFileId == gcodeFileId &&
                           (j.Status == PrintJobStatus.Queued ||
                            j.Status == PrintJobStatus.Assigned ||
                            j.Status == PrintJobStatus.Starting ||
                            j.Status == PrintJobStatus.Printing))
                .CountAsync(ct);
        }
    }
}
