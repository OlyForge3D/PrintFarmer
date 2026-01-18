using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Telemetry;

namespace Farm.Web.Api.Services.Queue
{
    /// <summary>
    /// Service for managing the print job queue and queue operations across printers.
    /// </summary>
    /// <remarks>
    /// This service orchestrates print job queue management, including:
    /// - Queue overview across all available printers
    /// - Per-printer queue retrieval and status reporting
    /// - Job assignment and priority management
    /// - Queue position calculation and priority-based ordering
    /// - Estimated completion time calculations
    /// - Job status transitions (queued, assigned, starting, printing, completed)
    /// - Comprehensive logging of all queue operations for debugging and analysis
    /// 
    /// The service uses IQueueDataService for specialized data queries and IQueueRepository
    /// for persistence operations, maintaining proper separation of concerns.
    /// </remarks>
    public class JobQueueService : IJobQueueService
    {
        private readonly IQueueRepository _repo;
        private readonly IQueueDataService _dataService;
        private readonly IUnifiedLoggingService _logger;

        /// <summary>
        /// Initializes a new instance of the JobQueueService with required dependencies.
        /// </summary>
        /// <param name="repo">Repository for print job persistence and CRUD operations</param>
        /// <param name="dataService">Specialized data service for queue-specific queries</param>
        /// <param name="logger">Unified logging service for operation tracking and audit trails</param>
        /// <exception cref="ArgumentNullException">Thrown when any required dependency is null</exception>
        public JobQueueService(
            IQueueRepository repo,
            IQueueDataService dataService,
            IUnifiedLoggingService logger)
        {
            ArgumentNullException.ThrowIfNull(repo);
            ArgumentNullException.ThrowIfNull(dataService);
            ArgumentNullException.ThrowIfNull(logger);
            _repo = repo;
            _dataService = dataService;
            _logger = logger;
        }

        /// <summary>
        /// Retrieves a comprehensive overview of the print job queue across all available printers.
        /// </summary>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Read-only list of QueueOverviewDto objects, one per printer, containing queue statistics and job information</returns>
        /// <remarks>
        /// This method provides a high-level view of queue status across the entire fleet of printers.
        /// For each available printer, it includes queue count, currently printing job information, and
        /// estimated completion time. Used for dashboard displays and queue status monitoring.
        /// </remarks>
        public async Task<IReadOnlyList<QueueOverviewDto>> GetQueueOverviewAsync(CancellationToken ct)
        {
            List<Printer> printers = await _dataService.GetAvailablePrintersAsync(ct);
            List<QueueOverviewDto> overview = [];

            foreach (Printer printer in printers)
            {
                List<PrintJob> queuedJobs = await _dataService.GetPrintJobsForPrinterAsync(printer.Id, ct);
                PrintJob? currentJob = await _dataService.GetCurrentJobForPrinterAsync(printer.Id, ct);

                overview.Add(new QueueOverviewDto
                {
                    PrinterId = printer.Id,
                    PrinterName = printer.Name,
                    PrinterModel = printer.Model?.Name ?? "Unknown",
                    IsAvailable = printer.IsAvailable,
                    QueuedJobsCount = queuedJobs.Count,
                    CurrentJobId = currentJob?.Id,
                    CurrentJobName = currentJob?.Name,
                    EstimatedCompletionTime = CalculateEstimatedCompletionTime(queuedJobs, currentJob)
                });
            }

            return overview;
        }

        /// <summary>
        /// Retrieves all print jobs in the queue for a specific printer, ordered by status and priority.
        /// </summary>
        /// <param name="printerId">Unique identifier of the printer whose queue to retrieve</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Read-only list of JobQueuePrintJobDto objects ordered by execution status and priority, with queue positions assigned</returns>
        /// <remarks>
        /// This method retrieves the complete job queue for a printer. Jobs are ordered with currently executing/starting
        /// jobs first, followed by queued jobs ordered by priority and then by queue time (FIFO within same priority).
        /// Queue positions are automatically calculated and assigned to each job in the result.
        /// </remarks>
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
                GcodeFileName = j.GcodeFile?.FileName ?? string.Empty,
                AssignedPrinterName = j.AssignedPrinter?.Name ?? string.Empty
            }).ToList();

            List<JobQueuePrintJobDto> queued = dtos.Where(d => d.Status.HasValue && (d.Status.Value == Farm.Infrastructure.PrintJobStatus.Queued || d.Status.Value == Farm.Infrastructure.PrintJobStatus.Assigned)).ToList();
            for (int i = 0; i < queued.Count; i++)
            {
                queued[i].QueuePosition = i + 1;
            }

            return dtos;
        }

        /// <summary>
        /// Adds a new print job to the queue, assigning it to a printer and calculating queue position.
        /// </summary>
        /// <param name="request">Queue job request containing gcode file ID, assigned printer, and job requirements (nozzle diameter, material type)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>JobQueuePrintJobDto with assigned printer and queue position on success; null if gcode file not found or no suitable printer available</returns>
        /// <remarks>
        /// This method creates a new print job and adds it to the queue. If no specific printer is assigned in the request,
        /// the system automatically selects the best available printer based on nozzle diameter and material type compatibility.
        /// If no compatible printer is available, the operation returns null. The job is assigned a queue position based on
        /// the next available position for its assigned printer. Job status defaults to Queued.
        /// </remarks>
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
                Name = gcode.FileName,
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
                GcodeFileName = gcode.FileName,
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

        /// <summary>
        /// Retrieves a specific print job from the queue by its unique identifier.
        /// </summary>
        /// <param name="id">Unique identifier of the print job to retrieve</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>JobQueuePrintJobDto with complete job information including gcode file name, assigned printer, and timing data; null if not found</returns>
        /// <remarks>
        /// This method retrieves a single job with all related information including gcode file details,
        /// assigned printer name, and both estimated and actual timing/filament usage data. Returns null
        /// if the specified job ID does not exist in the queue.
        /// </remarks>
        public async Task<JobQueuePrintJobDto?> GetJobAsync(Guid id, CancellationToken ct)
        {
            PrintJob? job = await _dataService.GetPrintJobByIdAsync(id, ct);
            return job == null
                ? null
                : new JobQueuePrintJobDto
                {
                    Id = job.Id,
                    GcodeFileId = job.GcodeFileId,
                    GcodeFileName = job.GcodeFile?.FileName ?? string.Empty,
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

        /// <summary>
        /// Removes a print job from the queue, making it unavailable for execution.
        /// </summary>
        /// <param name="id">Unique identifier of the print job to remove</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>True if job was successfully removed; false if job not found or cannot be removed (not in queued/assigned status)</returns>
        /// <remarks>
        /// This method removes a job from the queue. Jobs can only be removed if they are in Queued or Assigned status.
        /// Jobs that are currently printing, starting, or have already completed cannot be removed. Returns false if the
        /// job does not exist or if its current status does not permit removal.
        /// </remarks>
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

        /// <summary>
        /// Updates the priority level of a queued print job, affecting its execution order.
        /// </summary>
        /// <param name="id">Unique identifier of the print job to update</param>
        /// <param name="request">Update request containing the new priority level</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Updated JobQueuePrintJobDto with new priority; null if job not found</returns>
        /// <remarks>
        /// This method updates the priority of a queued job. Higher priority jobs execute before lower priority jobs
        /// within the same printer's queue. The update timestamp is automatically set to the current UTC time.
        /// Returns null if the specified job ID does not exist. Priority changes are effective immediately.
        /// </remarks>
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
                GcodeFileName = job.GcodeFile?.FileName ?? string.Empty,
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

        /// <summary>
        /// Updates the status, priority, assignment, or completion metrics of a print job.
        /// </summary>
        /// <param name="id">Unique identifier of the print job to update</param>
        /// <param name="request">Update request containing fields to modify (status, priority, assigned printer, filament usage, failure reason)</param>
        /// <param name="ct">Cancellation token for async operation</param>
        /// <returns>Updated JobQueuePrintJobDto with current state; null if job not found or assigned printer validation fails</returns>
        /// <remarks>
        /// This method provides comprehensive job update capability, allowing modification of multiple job properties:
        /// - Status: Job execution state (queued, assigned, starting, printing, completed, failed)
        /// - Priority: Queue priority level affecting execution order
        /// - Assigned Printer: Reassignment to a different printer (validates printer exists)
        /// - Actual Filament Usage: Recorded usage for completed/failed jobs
        /// - Failure Reason: Description of failure conditions for failed jobs
        /// 
        /// All fields in the update request are optional. Only provided fields are modified. The update timestamp
        /// is automatically set to current UTC time. Printer assignment changes trigger a reload of the complete job data
        /// to ensure printer information is current. Returns null if job not found or if assigned printer ID is invalid.
        /// </remarks>
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
                GcodeFileName = job.GcodeFile?.FileName ?? string.Empty,
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
                // Check nozzle diameter - now per-toolhead, check if any toolhead matches
                if (request.RequiredNozzleDiameter.HasValue)
                {
                    double requiredDiameter = (double)request.RequiredNozzleDiameter;
                    bool hasCompatibleToolhead = printer.Toolheads?.Any(t => t.NozzleDiameter.HasValue && Math.Abs(t.NozzleDiameter.Value - requiredDiameter) <= 0.01) ?? false;
                    if (!hasCompatibleToolhead)
                    {
                        continue;
                    }
                }

                // Check supported materials - now per-toolhead, check if any toolhead supports the material
                if (!string.IsNullOrEmpty(request.RequiredMaterialType))
                {
                    bool hasCompatibleToolhead = printer.Toolheads?.Any(t => t.SupportedMaterials?.Contains(request.RequiredMaterialType) ?? false) ?? false;
                    if (!hasCompatibleToolhead)
                    {
                        continue;
                    }
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
