using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Repositories.Queue
{
    public interface IQueueRepository
    {
        Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct);
        Task<List<PrintJob>> GetPrintJobsForPrinterAsync(Guid printerId, CancellationToken ct);
        Task<PrintJob?> GetCurrentJobForPrinterAsync(Guid printerId, CancellationToken ct);
        Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct);
        Task AddPrintJobAsync(PrintJob job, CancellationToken ct);
        Task<PrintJob?> GetPrintJobByIdAsync(Guid id, CancellationToken ct);
        Task RemovePrintJobAsync(PrintJob job, CancellationToken ct);
        Task<int> CountQueuedJobsForPrinterAsync(Guid printerId, CancellationToken ct);
        Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct);
        // Get all print jobs in the queue (with includes) for administrative listing
        Task<List<PrintJob>> GetAllPrintJobsAsync(CancellationToken ct);
        // Get next global queue position (for unassigned/global queue)
        Task<int> GetNextGlobalQueuePositionAsync(CancellationToken ct);
        // Count active jobs that reference a particular gcode file
        Task<int> CountActiveJobsUsingGcodeAsync(Guid gcodeFileId, CancellationToken ct);
        Task SaveChangesAsync(CancellationToken ct);
    }
}
