using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Queue
{
    public class JobQueueService : IJobQueueService
    {
        private readonly IQueueRepository _repo;
        private readonly IQueueDataService _dataService;

        public JobQueueService(IQueueRepository repo, IQueueDataService dataService)
        {
            ArgumentNullException.ThrowIfNull(repo);
            ArgumentNullException.ThrowIfNull(dataService);
            _repo = repo;
            _dataService = dataService;
        }

        public async Task<IReadOnlyList<QueueOverviewDto>> GetQueueOverviewAsync(CancellationToken ct)
        {
            List<Printer> printers = await _dataService.GetAvailablePrintersAsync(ct);
            List<QueueOverviewDto> overview = new List<QueueOverviewDto>();

            foreach (Printer printer in printers)
            {
                List<PrintJob> queuedJobs = await _dataService.GetPrintJobsForPrinterAsync(printer.Id, ct);
                PrintJob? currentJob = await _dataService.GetCurrentJobForPrinterAsync(printer.Id, ct);

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
            List<PrintJob> jobs = await _dataService.GetPrintJobsForPrinterAsync(printerId, ct);

            List<JobQueuePrintJobDto> dtos = jobs.Select(j => new JobQueuePrintJobDto
            {
                Id = j.Id,
                GcodeFileId = j.GcodeFileId,
                AssignedPrinterId = j.AssignedPrinterId,
                Status = (PrintJobStatus?)j.Status,
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

            List<JobQueuePrintJobDto> queued = dtos.Where(d => d.Status.HasValue && (d.Status.Value == Farm.Infrastructure.PrintJobStatus.Queued || d.Status.Value == Farm.Infrastructure.PrintJobStatus.Assigned)).ToList();
            for (int i = 0; i < queued.Count; i++)
            {
                queued[i].QueuePosition = i + 1;
            }

            return dtos;
        }

        public async Task<JobQueuePrintJobDto?> AddJobToQueueAsync(QueuePrintJobDto request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            GcodeFile? gcode = await _dataService.GetGcodeFileAsync(request.GcodeFileId, ct);
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

            PrintJob job = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = gcode.DisplayName,
                GcodeFileId = request.GcodeFileId,
                AssignedPrinterId = assignedPrinterId,
                Status = PrintJobStatus.Queued,
                Priority = (int)request.Priority,
                QueuePosition = await _dataService.GetNextQueuePositionAsync(assignedPrinterId.Value, ct),
                RequiredNozzleDiameter = request.RequiredNozzleDiameter,
                RequiredMaterialType = request.RequiredMaterialType,
                EstimatedPrintTime = gcode.EstimatedPrintTimeMinutes.HasValue ? TimeSpan.FromMinutes(gcode.EstimatedPrintTimeMinutes.Value) : null,
                EstimatedFilamentUsage = gcode.EstimatedFilamentWeightG,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };

            await _repo.AddAsync(job, ct);
            await _repo.SaveChangesAsync(ct);

            return new JobQueuePrintJobDto
            {
                Id = job.Id,
                GcodeFileId = job.GcodeFileId,
                GcodeFileName = gcode.DisplayName,
                AssignedPrinterId = job.AssignedPrinterId,
                AssignedPrinterName = (await _dataService.GetAvailablePrintersAsync(ct)).Find(p => p.Id == job.AssignedPrinterId)?.Name ?? "Unknown",
                Status = (PrintJobStatus?)job.Status,
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
            PrintJob? job = await _dataService.GetPrintJobByIdAsync(id, ct);
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
                Status = (PrintJobStatus?)job.Status,
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
            PrintJob? job = await _dataService.GetPrintJobByIdAsync(id, ct);
            if (job == null)
            {
                return false;
            }

            if (job.Status != PrintJobStatus.Queued && job.Status != PrintJobStatus.Assigned)
            {
                return false;
            }

            await _repo.RemoveAsync(job, ct);
            await _repo.SaveChangesAsync(ct);
            return true;
        }

        public async Task<JobQueuePrintJobDto?> UpdateJobPriorityAsync(Guid id, UpdateJobPriorityDto request, CancellationToken ct)
        {
            PrintJob? job = await _dataService.GetPrintJobByIdAsync(id, ct);
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
                Status = (PrintJobStatus?)job.Status,
                Priority = job.Priority,
                QueuePosition = job.QueuePosition,
                EstimatedPrintTime = job.EstimatedPrintTime,
                EstimatedFilamentUsage = job.EstimatedFilamentUsage,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.UpdatedAt
            };
        }

        public async Task<JobQueuePrintJobDto?> UpdateJobAsync(Guid id, UpdatePrintJobStatusDto request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            PrintJob? job = await _dataService.GetPrintJobByIdAsync(id, ct);
            if (job == null)
            {
                return null;
            }

            // Update fields if provided
            if (request.Status.HasValue)
            {
                job.Status = (PrintJobStatus)(int)request.Status.Value;
            }

            if (request.Priority.HasValue)
            {
                job.Priority = (int)request.Priority.Value;
            }

            if (request.AssignedPrinterId.HasValue)
            {
                List<Printer> printer = await _dataService.GetAvailablePrintersAsync(ct);
                // Validate printer exists
                Printer? found = printer.Find(p => p.Id == request.AssignedPrinterId.Value);
                if (found == null)
                {
                    return null; // caller will translate to BadRequest
                }
                job.AssignedPrinterId = request.AssignedPrinterId.Value;
            }

            if (request.ActualFilamentUsage.HasValue)
            {
                job.ActualFilamentUsage = request.ActualFilamentUsage.Value;
            }

            if (!string.IsNullOrEmpty(request.FailureReason))
            {
                job.FailureReason = request.FailureReason;
            }

            job.UpdatedAt = DateTime.UtcNow;

            await _repo.SaveChangesAsync(ct);

            // Reload printer if assignment changed
            if (request.AssignedPrinterId.HasValue)
            {
                job = await _dataService.GetPrintJobByIdAsync(id, ct);
            }

            return new JobQueuePrintJobDto
            {
                Id = job!.Id,
                GcodeFileId = job.GcodeFileId,
                GcodeFileName = job.GcodeFile?.DisplayName ?? string.Empty,
                AssignedPrinterId = job.AssignedPrinterId,
                AssignedPrinterName = job.AssignedPrinter?.Name ?? string.Empty,
                Status = (PrintJobStatus?)job.Status,
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

        private async Task<Guid?> FindBestAvailablePrinterAsync(QueuePrintJobDto request, CancellationToken ct)
        {
            List<Printer> printers = await _dataService.GetAvailablePrintersAsync(ct);

            foreach (Printer printer in printers)
            {
                if (request.RequiredNozzleDiameter.HasValue && printer.Capabilities?.NozzleDiameter.HasValue == true && Math.Abs(printer.Capabilities.NozzleDiameter.Value - (double)request.RequiredNozzleDiameter.Value) > 0.01)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(request.RequiredMaterialType) && printer.Capabilities?.SupportedMaterials != null && !printer.Capabilities.SupportedMaterials.Contains(request.RequiredMaterialType))
                {
                    continue;
                }

                int queueCount = await _dataService.CountQueuedJobsForPrinterAsync(printer.Id, ct);
                if (queueCount < 5)
                {
                    return printer.Id;
                }
            }

            return null;
        }

        private static DateTime? CalculateEstimatedCompletionTime(List<PrintJob> queuedJobs, PrintJob? currentJob)
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
