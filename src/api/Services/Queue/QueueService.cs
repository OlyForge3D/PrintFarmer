using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Repositories.Queue;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Queue
{
    public class QueueService : IQueueService
    {
        private readonly IQueueRepository _repo;
        private readonly IUnifiedLoggingService _logger;

        public QueueService(IQueueRepository repo, IUnifiedLoggingService logger)
        {
            ArgumentNullException.ThrowIfNull(repo);
            ArgumentNullException.ThrowIfNull(logger);
            _repo = repo;
            _logger = logger;
        }

        public async Task<IReadOnlyList<QueueOverviewDto>> GetQueueOverviewAsync(CancellationToken ct)
        {
            var printers = await _repo.GetAvailablePrintersAsync(ct);
            var overview = new List<QueueOverviewDto>();

            foreach (var printer in printers)
            {
                var queuedJobs = await _repo.GetPrintJobsForPrinterAsync(printer.Id, ct);
                var currentJob = await _repo.GetCurrentJobForPrinterAsync(printer.Id, ct);

                overview.Add(new QueueOverviewDto
                {
                    PrinterId = printer.Id,
                    PrinterName = printer.Name,
                    PrinterModel = printer.Model?.Name ?? "Unknown",
                    IsAvailable = printer.Capabilities?.IsAvailable ?? false,
                    QueuedJobsCount = queuedJobs.Count,
                    CurrentJobId = currentJob?.Id,
                    CurrentJobName = currentJob?.Name,
                    EstimatedCompletionTime = CalculateEstimatedCompletionTime(queuedJobs, currentJob)
                });
            }

            return overview;
        }

        public async Task<IReadOnlyList<JobQueuePrintJobDto>> GetPrinterQueueAsync(Guid printerId, CancellationToken ct)
        {
            var jobs = await _repo.GetPrintJobsForPrinterAsync(printerId, ct);

            var dtos = jobs.Select(j => new JobQueuePrintJobDto
            {
                Id = j.Id,
                GcodeFileId = j.GcodeFileId,
                AssignedPrinterId = j.AssignedPrinterId,
                Status = (Farm.Web.Shared.PrintJobStatus?)j.Status,
                Priority = j.Priority,
                QueuePosition = 0,
                RequiredNozzleDiameter = j.RequiredNozzleDiameter,
                RequiredMaterialType = j.RequiredMaterialType,
                EstimatedPrintTime = j.EstimatedPrintTime,
                EstimatedFilamentUsage = j.EstimatedFilamentUsage,
                ActualStartTime = j.ActualStartTime,
                ActualEndTime = j.ActualEndTime,
                ActualPrintTime = j.ActualPrintTime,
                ActualFilamentUsage = j.ActualFilamentUsage,
                FailureReason = j.FailureReason,
                CreatedAt = j.CreatedAt,
                UpdatedAt = j.UpdatedAt,
                GcodeFileName = j.GcodeFile?.DisplayName ?? string.Empty,
                AssignedPrinterName = j.AssignedPrinter?.Name ?? string.Empty
            }).ToList();

            var queued = dtos.Where(d => d.Status.HasValue && (d.Status.Value == Farm.Web.Shared.PrintJobStatus.Queued || d.Status.Value == Farm.Web.Shared.PrintJobStatus.Assigned)).ToList();
            for (int i = 0; i < queued.Count; i++)
            {
                queued[i].QueuePosition = i + 1;
            }

            return dtos;
        }

        public async Task<JobQueuePrintJobDto?> AddJobToQueueAsync(QueuePrintJobDto request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            var gcode = await _repo.GetGcodeFileAsync(request.GcodeFileId, ct);
            if (gcode == null)
            {
                return null;
            }

            Guid? assignedPrinterId = request.AssignedPrinterId;
            if (assignedPrinterId == null)
            {
                assignedPrinterId = await FindBestAvailablePrinterAsync(request, ct);
                if (assignedPrinterId == null)
                {
                    return null;
                }
            }

            var job = new Farm.Infrastructure.Domain.PrintJob
            {
                Id = Guid.NewGuid(),
                Name = gcode.DisplayName,
                GcodeFileId = request.GcodeFileId,
                AssignedPrinterId = assignedPrinterId,
                Status = PrintJobStatus.Queued,
                Priority = (int)request.Priority,
                QueuePosition = await _repo.GetNextQueuePositionAsync(assignedPrinterId.Value, ct),
                RequiredNozzleDiameter = request.RequiredNozzleDiameter,
                RequiredMaterialType = request.RequiredMaterialType,
                EstimatedPrintTime = gcode.EstimatedPrintTimeMinutes.HasValue ? TimeSpan.FromMinutes(gcode.EstimatedPrintTimeMinutes.Value) : null,
                EstimatedFilamentUsage = gcode.EstimatedFilamentWeightG,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };

            await _repo.AddPrintJobAsync(job, ct);
            await _repo.SaveChangesAsync(ct);

            return new JobQueuePrintJobDto
            {
                Id = job.Id,
                GcodeFileId = job.GcodeFileId,
                GcodeFileName = gcode.DisplayName,
                AssignedPrinterId = job.AssignedPrinterId,
                AssignedPrinterName = (await _repo.GetAvailablePrintersAsync(ct)).Find(p => p.Id == job.AssignedPrinterId)?.Name ?? "Unknown",
                Status = (Farm.Web.Shared.PrintJobStatus?)job.Status,
                Priority = job.Priority,
                QueuePosition = job.QueuePosition,
                RequiredNozzleDiameter = job.RequiredNozzleDiameter,
                RequiredMaterialType = job.RequiredMaterialType,
                EstimatedPrintTime = job.EstimatedPrintTime,
                EstimatedFilamentUsage = job.EstimatedFilamentUsage,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.UpdatedAt
            };
        }

        public async Task<JobQueuePrintJobDto?> GetJobAsync(Guid id, CancellationToken ct)
        {
            var job = await _repo.GetPrintJobByIdAsync(id, ct);
            if (job == null)
            {
                return null;
            }

            return new JobQueuePrintJobDto
            {
                Id = job.Id,
                GcodeFileId = job.GcodeFileId,
                GcodeFileName = job.GcodeFile?.DisplayName ?? string.Empty,
                AssignedPrinterId = job.AssignedPrinterId,
                AssignedPrinterName = job.AssignedPrinter?.Name ?? "Unknown",
                Status = (Farm.Web.Shared.PrintJobStatus?)job.Status,
                Priority = job.Priority,
                QueuePosition = job.QueuePosition,
                RequiredNozzleDiameter = job.RequiredNozzleDiameter,
                RequiredMaterialType = job.RequiredMaterialType,
                EstimatedPrintTime = job.EstimatedPrintTime,
                EstimatedFilamentUsage = job.EstimatedFilamentUsage,
                ActualStartTime = job.ActualStartTime,
                ActualEndTime = job.ActualEndTime,
                ActualPrintTime = job.ActualPrintTime,
                ActualFilamentUsage = job.ActualFilamentUsage,
                FailureReason = job.FailureReason,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.UpdatedAt
            };
        }

        public async Task<bool> RemoveJobAsync(Guid id, CancellationToken ct)
        {
            var job = await _repo.GetPrintJobByIdAsync(id, ct);
            if (job == null)
            {
                return false;
            }

            if (job.Status != PrintJobStatus.Queued && job.Status != PrintJobStatus.Assigned)
            {
                return false;
            }

            await _repo.RemovePrintJobAsync(job, ct);
            await _repo.SaveChangesAsync(ct);
            return true;
        }

        public async Task<JobQueuePrintJobDto?> UpdateJobPriorityAsync(Guid id, UpdateJobPriorityDto request, CancellationToken ct)
        {
            var job = await _repo.GetPrintJobByIdAsync(id, ct);
            if (job == null)
            {
                return null;
            }
            job.Priority = request.Priority;
            job.UpdatedAt = DateTime.UtcNow;
            await _repo.SaveChangesAsync(ct);

            return new JobQueuePrintJobDto
            {
                Id = job.Id,
                GcodeFileId = job.GcodeFileId,
                GcodeFileName = job.GcodeFile?.DisplayName ?? string.Empty,
                AssignedPrinterId = job.AssignedPrinterId,
                AssignedPrinterName = job.AssignedPrinter?.Name ?? "Unknown",
                Status = (Farm.Web.Shared.PrintJobStatus?)job.Status,
                Priority = job.Priority,
                QueuePosition = job.QueuePosition,
                EstimatedPrintTime = job.EstimatedPrintTime,
                EstimatedFilamentUsage = job.EstimatedFilamentUsage,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.UpdatedAt
            };
        }

        private async Task<Guid?> FindBestAvailablePrinterAsync(QueuePrintJobDto request, CancellationToken ct)
        {
            var printers = await _repo.GetAvailablePrintersAsync(ct);

            foreach (var printer in printers)
            {
                if (request.RequiredNozzleDiameter.HasValue && printer.Capabilities?.NozzleDiameter.HasValue == true && Math.Abs(printer.Capabilities.NozzleDiameter.Value - (double)request.RequiredNozzleDiameter.Value) > 0.01)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(request.RequiredMaterialType) && printer.Capabilities?.SupportedMaterials != null && !printer.Capabilities.SupportedMaterials.Contains(request.RequiredMaterialType))
                {
                    continue;
                }

                int queueCount = await _repo.CountQueuedJobsForPrinterAsync(printer.Id, ct);
                if (queueCount < 5)
                {
                    return printer.Id;
                }
            }

            return null;
        }

        private static DateTime? CalculateEstimatedCompletionTime(List<Farm.Infrastructure.Domain.PrintJob> queuedJobs, Farm.Infrastructure.Domain.PrintJob? currentJob)
        {
            double totalMinutes = 0.0;

            if (currentJob?.EstimatedPrintTime.HasValue == true)
            {
                TimeSpan elapsed = currentJob.ActualStartTime.HasValue ? DateTime.UtcNow - currentJob.ActualStartTime.Value : TimeSpan.Zero;
                TimeSpan remaining = currentJob.EstimatedPrintTime.Value - elapsed;
                totalMinutes += Math.Max(0, remaining.TotalMinutes);
            }

            totalMinutes += queuedJobs.Where(j => j.EstimatedPrintTime.HasValue).Sum(j => j.EstimatedPrintTime!.Value.TotalMinutes);

            return totalMinutes > 0 ? DateTime.UtcNow.AddMinutes(totalMinutes) : null;
        }
    }
}
