using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Repositories.Queue
{
    public class EfQueueRepository : IQueueRepository
    {
        private readonly AppDbContext _db;

        public EfQueueRepository(AppDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct)
        {
            return _db.Printers
                .Include(p => p.Capabilities)
                .Include(p => p.Model)
                .Where(p => p.Capabilities != null && p.Capabilities.IsAvailable)
                .ToListAsync(ct);
        }

        public Task<List<PrintJob>> GetPrintJobsForPrinterAsync(Guid printerId, CancellationToken ct)
        {
            return _db.PrintJobs
                .Include(j => j.GcodeFile)
                .Include(j => j.AssignedPrinter)
                .Where(j => j.AssignedPrinterId == printerId)
                .OrderBy(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting ? 0 : 1)
                .ThenBy(j => j.Priority)
                .ThenBy(j => j.QueuedAt)
                .ToListAsync(ct);
        }

        public Task<List<PrintJob>> GetAllPrintJobsAsync(CancellationToken ct)
        {
            return _db.PrintJobs
                .Include(j => j.GcodeFile)
                .Include(j => j.AssignedPrinter)
                .OrderBy(j => j.QueuePosition)
                .ThenBy(j => j.CreatedAt)
                .ToListAsync(ct);
        }

        public Task<PrintJob?> GetCurrentJobForPrinterAsync(Guid printerId, CancellationToken ct)
        {
            return _db.PrintJobs
                .FirstOrDefaultAsync(j => j.AssignedPrinterId == printerId && (j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Printing), ct);
        }

        public Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct)
        {
            return _db.GcodeFiles.FindAsync(new object[] { id }, ct).AsTask();
        }

        public Task AddPrintJobAsync(PrintJob job, CancellationToken ct)
        {
            _db.PrintJobs.Add(job);
            return Task.CompletedTask;
        }

        public Task<PrintJob?> GetPrintJobByIdAsync(Guid id, CancellationToken ct)
        {
            return _db.PrintJobs.Include(j => j.GcodeFile).Include(j => j.AssignedPrinter).FirstOrDefaultAsync(j => j.Id == id, ct);
        }

        public Task RemovePrintJobAsync(PrintJob job, CancellationToken ct)
        {
            _db.PrintJobs.Remove(job);
            return Task.CompletedTask;
        }

        public Task<int> CountQueuedJobsForPrinterAsync(Guid printerId, CancellationToken ct)
        {
            return _db.PrintJobs.CountAsync(j => j.AssignedPrinterId == printerId && (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned), ct);
        }

        public async Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct)
        {
            int? max = await _db.PrintJobs.Where(j => j.AssignedPrinterId == printerId && (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned)).MaxAsync(j => (int?)j.QueuePosition, ct);
            return (max ?? 0) + 1;
        }

        public async Task<int> GetNextGlobalQueuePositionAsync(CancellationToken ct)
        {
            int? max = await _db.PrintJobs.Where(j => j.Status == PrintJobStatus.Queued).MaxAsync(j => (int?)j.QueuePosition, ct);
            return (max ?? 0) + 1;
        }

        public Task<int> CountActiveJobsUsingGcodeAsync(Guid gcodeFileId, CancellationToken ct)
        {
            return _db.PrintJobs
                .Where(j => j.GcodeFileId == gcodeFileId &&
                           (j.Status == PrintJobStatus.Queued ||
                            j.Status == PrintJobStatus.Assigned ||
                            j.Status == PrintJobStatus.Starting ||
                            j.Status == PrintJobStatus.Printing))
                .CountAsync(ct);
        }

        public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
    }
}
