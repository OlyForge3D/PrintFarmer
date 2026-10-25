using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>Narrow shared-context admission; caller owns the operation/job transaction.</summary>
public interface IPrinterFileQueueAdmission
{
    Task<JobQueuePrintJobDto?> AddAsync(
        PrinterFileOperation operation, QueuePrintJobDto request, CancellationToken ct);

    Task NotifyCommittedAsync(Guid printerId, CancellationToken ct);
}
