using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.PrintJobQueue;

public interface IPrintJobQueueService
{
    Task<IEnumerable<PrintJobDto>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<PrintJobDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PrintJobDto?> EnqueueAsync(EnqueuePrintJobRequest req, CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default);
}

public record EnqueuePrintJobRequest
(
    [property: System.Text.Json.Serialization.JsonRequired] Guid gcodeFileId,
    Guid? assignedPrinterId,
    string? priority,
    double? requiredNozzleDiameter,
    string? requiredMaterialType);

public record PrintJobDto
(
    Guid Id,
    Guid GcodeFileId,
    string GcodeFileName,
    Guid? AssignedPrinterId,
    string? AssignedPrinterName,
    string Status,
    int QueuePosition,
    double? RequiredNozzleDiameter,
    string? RequiredMaterialType,
    DateTime CreatedAt);
