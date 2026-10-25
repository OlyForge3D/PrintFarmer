using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Queue;

public partial class JobQueueService
{
    /// <summary>
    /// The linked job, pin and operation transition use the same context and transaction.
    /// The ordinary public queue contract cannot supply an operation or bypass this boundary.
    /// </summary>
    public Task<JobQueuePrintJobDto?> AddAsync(
        PrinterFileOperation operation, QueuePrintJobDto request, CancellationToken ct)
    {
        if (_db is null || _db.Database.CurrentTransaction is null ||
            _db.Entry(operation).State == EntityState.Detached ||
            operation.Action != PrinterFileAction.Queue ||
            operation.State != PrinterFileOperationState.Enqueueing ||
            operation.ActorId == Guid.Empty || operation.CancellationRequested ||
            operation.Jobs.Count != 0 ||
            request.GcodeFileId != operation.GcodeFileId ||
            request.AssignedPrinterId != operation.DestinationPrinterId ||
            operation.ContentSha256 is null || operation.SizeBytes is not > 0)
        {
            throw new InvalidOperationException("Catalog queue admission requires the owning fenced transaction.");
        }

        return AddJobCoreAsync(request, operation.ActorId, operation, ct);
    }
}
