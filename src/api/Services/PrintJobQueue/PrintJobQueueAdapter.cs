using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.PrintJobQueue;
using Farm.Web.Api.Services.Queue;

namespace Farm.Web.Api.Services.PrintJobQueue;

public class PrintJobQueueAdapter(Services.Queue.IJobQueueService jobQueueService) : IPrintJobQueueService
{
    private readonly Services.Queue.IJobQueueService _jobQueueService = jobQueueService;

    public async Task<IEnumerable<PrintJobDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var list = await _jobQueueService.GetQueueOverviewAsync(cancellationToken).ConfigureAwait(false);
        return list.Select(q => new PrintJobDto(
            Id: Guid.NewGuid(),
            GcodeFileId: Guid.Empty,
            GcodeFileName: q.PrinterName ?? string.Empty,
            AssignedPrinterId: q.PrinterId,
            AssignedPrinterName: q.PrinterName,
            Status: q.IsAvailable ? "Available" : "Unavailable",
            QueuePosition: q.QueuedJobsCount,
            RequiredNozzleDiameter: null,
            RequiredMaterialType: null,
            CreatedAt: DateTime.UtcNow
        ));
    }

    public async Task<PrintJobDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var dto = await _jobQueueService.GetJobAsync(id, cancellationToken).ConfigureAwait(false);
        if (dto == null)
        {
            return null;
        }

        double? nozzle = dto.RequiredNozzleDiameter.HasValue ? (double?)Convert.ToDouble(dto.RequiredNozzleDiameter.Value) : null;
        return new PrintJobDto(dto.Id, dto.GcodeFileId, dto.GcodeFileName, dto.AssignedPrinterId ?? Guid.Empty, dto.AssignedPrinterName, dto.Status?.ToString() ?? string.Empty, dto.QueuePosition, nozzle, dto.RequiredMaterialType, dto.CreatedAt);
    }

    public async Task<PrintJobDto?> EnqueueAsync(EnqueuePrintJobRequest req, CancellationToken cancellationToken = default)
    {
        var qreq = new QueuePrintJobDto
        {
            GcodeFileId = req.gcodeFileId,
            AssignedPrinterId = req.assignedPrinterId,
            Priority = ParsePriority(req.priority),
            RequiredNozzleDiameter = req.requiredNozzleDiameter.HasValue ? (decimal?)Convert.ToDecimal(req.requiredNozzleDiameter.Value) : null,
            RequiredMaterialType = req.requiredMaterialType
        };

        var added = await _jobQueueService.AddJobToQueueAsync(qreq, cancellationToken).ConfigureAwait(false);
        if (added == null)
        {
            return null;
        }

        double? reqNozzle = added.RequiredNozzleDiameter.HasValue ? (double?)Convert.ToDouble(added.RequiredNozzleDiameter.Value) : null;
        return new PrintJobDto(added.Id, added.GcodeFileId, added.GcodeFileName, added.AssignedPrinterId ?? Guid.Empty, added.AssignedPrinterName ?? string.Empty, added.Status?.ToString() ?? string.Empty, added.QueuePosition, reqNozzle, added.RequiredMaterialType, added.CreatedAt);
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _jobQueueService.RemoveJobAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private static PrintJobPriority ParsePriority(string? p)
    {
        return p?.ToLowerInvariant() switch
        {
            "low" => PrintJobPriority.Low,
            "normal" => PrintJobPriority.Normal,
            "high" => PrintJobPriority.High,
            "urgent" => PrintJobPriority.Urgent,
            _ => PrintJobPriority.Normal
        };
    }
}
