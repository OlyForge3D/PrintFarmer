using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Queue;

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
    private readonly ILogger<JobQueueService> _logger;
    private readonly IPrintCostCalculator? _costCalculator;
    private readonly IAutoDispatchTrigger? _dispatchTrigger;
    private readonly IAutoDispatchService? _autoDispatchService;

    /// <summary>
    /// Initializes a new instance of the JobQueueService with required dependencies.
    /// </summary>
    /// <param name="repo">Repository for print job persistence and CRUD operations</param>
    /// <param name="dataService">Specialized data service for queue-specific queries</param>
    /// <param name="logger">Unified logging service for operation tracking and audit trails</param>
    /// <param name="costCalculator">Optional cost calculator for estimating job costs from Spoolman data</param>
    /// <param name="dispatchTrigger">Optional dispatch trigger for notifying the auto-dispatch service</param>
    /// <param name="autoDispatchService">Optional auto-dispatch ready-gate service for triggering bed-clear confirmation on idle printers</param>
    /// <exception cref="ArgumentNullException">Thrown when any required dependency is null</exception>
    public JobQueueService(
        IQueueRepository repo,
        IQueueDataService dataService,
        ILogger<JobQueueService> logger,
        IPrintCostCalculator? costCalculator = null,
        IAutoDispatchTrigger? dispatchTrigger = null,
        IAutoDispatchService? autoDispatchService = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(dataService);
        ArgumentNullException.ThrowIfNull(logger);
        _repo = repo;
        _dataService = dataService;
        _logger = logger;
        _costCalculator = costCalculator;
        _dispatchTrigger = dispatchTrigger;
        _autoDispatchService = autoDispatchService;
    }

    /// <summary>
    /// Retrieves a comprehensive overview of the print job queue across available printers.
    /// When a required model is specified, only returns printers compatible with that model
    /// (matching either the canonical model name or a slicer-specific alias like "COREONEL").
    /// </summary>
    /// <param name="requiredModel">Optional printer model name or alias to filter by</param>
    /// <param name="requiredNozzle">Optional required nozzle diameter in mm (exact match ±0.01mm)</param>
    /// <param name="requiredMaterial">Optional required material type (case-insensitive)</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>Read-only list of QueueOverviewDto objects, one per compatible printer</returns>
    /// <remarks>
    /// This method provides a high-level view of queue status across the fleet of printers.
    /// For each available printer, it includes queue count, currently printing job information, and
    /// estimated completion time. Used for dashboard displays and queue status monitoring.
    ///
    /// All filtering is done server-side for consistency with auto-assign logic:
    /// - Model: Case-insensitive matching against model name and aliases
    /// - Nozzle: Exact match with ±0.01mm tolerance for floating point comparison
    /// - Material: Case-insensitive matching against any toolhead's supported materials
    /// </remarks>
    public async Task<IReadOnlyList<QueueOverviewDto>> GetQueueOverviewAsync(string? requiredModel, decimal? requiredNozzle, string? requiredMaterial, CancellationToken ct)
    {
        // Get printers - either all available or filtered by model compatibility
        List<Printer> printers = string.IsNullOrWhiteSpace(requiredModel)
            ? await _dataService.GetAvailablePrintersAsync(ct)
            : await _dataService.GetCompatiblePrintersAsync(requiredModel, ct);

        // Apply nozzle filter if specified (exact match with tolerance)
        // Only printers with a matching nozzle configured are included
        if (requiredNozzle.HasValue)
        {
            double required = (double)requiredNozzle.Value;
            printers = printers
                .Where(p => p.Toolheads?.Any(t =>
                    t.NozzleModel != null &&
                    Math.Abs(t.NozzleModel.Diameter - required) <= 0.01) ?? false)
                .ToList();
        }

        // Apply material filter if specified (case-insensitive)
        if (!string.IsNullOrWhiteSpace(requiredMaterial))
        {
            printers = printers
                .Where(p => p.Toolheads?.Any(t => t.SupportedMaterials?.Any(m => string.Equals(m, requiredMaterial, StringComparison.OrdinalIgnoreCase)) ?? false) ?? false)
                .ToList();
        }

        List<QueueOverviewDto> overview = [];

        // Batch-load all jobs for all printers in a SINGLE query (was 2N+1 queries before)
        List<Guid> printerIds = printers.Select(p => p.Id).ToList();
        List<PrintJob> allJobsForAllPrinters = await _dataService.GetPrintJobsForPrintersAsync(printerIds, ct);
        ILookup<Guid?, List<PrintJob>> jobsByPrinter = allJobsForAllPrinters
            .GroupBy(j => j.AssignedPrinterId)
            .ToLookup(g => g.Key, g => g.ToList());

        foreach (Printer printer in printers)
        {
            List<PrintJob> allJobs = jobsByPrinter.Contains(printer.Id)
                ? jobsByPrinter[printer.Id].First()
                : [];

            int queuedCount = allJobs.Count(j => j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned);
            PrintJob? currentJob = allJobs.FirstOrDefault(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting);

            // Get primary toolhead info (first toolhead or the one marked as primary)
            Toolhead? primaryToolhead = printer.Toolheads?.FirstOrDefault(t => t.IsPrimary)
                ?? printer.Toolheads?.FirstOrDefault();

            // Collect all supported materials from all toolheads
            var supportedMaterials = printer.Toolheads?
                .Where(t => t.SupportedMaterials != null)
                .SelectMany(t => t.SupportedMaterials!)
                .Distinct()
                .ToList();

            // Collect model aliases for compatibility matching (e.g., "COREONEL" -> "Prusa CORE One L")
            var modelAliases = printer.Model?.Aliases?
                .Select(a => a.SlicerModelName)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            overview.Add(new QueueOverviewDto
            {
                PrinterId = printer.Id,
                PrinterName = printer.Name,
                PrinterModel = printer.Model?.Name ?? "Unknown",
                ModelAliases = modelAliases,
                IsAvailable = printer.IsAvailable,
                QueuedJobsCount = queuedCount,
                CurrentJobId = currentJob?.Id,
                CurrentJobName = currentJob?.Name,
                EstimatedCompletionTime = CalculateEstimatedCompletionTime(allJobs, currentJob),
                NozzleDiameter = primaryToolhead?.NozzleModel?.Diameter,
                SupportedMaterials = supportedMaterials
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
            EstimatedCost = j.EstimatedCost,
            ActualCost = j.ActualCost,
            Copies = j.Copies,
            CompletedCopies = j.CompletedCopies,
            RemainingCopies = j.RemainingCopies,
            ProjectFileId = j.ProjectFileId,
            CreatedAt = j.CreatedAt,
            UpdatedAt = j.UpdatedAt,
            GcodeFileName = j.GcodeFile?.Name ?? string.Empty,
            AssignedPrinterName = j.AssignedPrinter?.Name ?? string.Empty,
            ToolheadUsages = MapToolheadUsages(j)
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

        // Merge request values with G-code file metadata (request takes precedence, G-code as fallback)
        // This ensures the same matching logic works for:
        // 1. OctoPrint upload+print (slicer sends values in request)
        // 2. Manual queue from UI (may not include values - use G-code metadata)
        // 3. Direct API calls (can specify overrides or rely on G-code metadata)
        // Build effective request: request values take precedence, G-code metadata as fallback
        QueuePrintJobDto effectiveRequest = new QueuePrintJobDto
        {
            GcodeFileId = request.GcodeFileId,
            AssignedPrinterId = request.AssignedPrinterId,
            Priority = request.Priority,
            RequiredNozzleDiameter = request.RequiredNozzleDiameter ?? (decimal?)gcode.RequiredNozzleDiameter,
            RequiredMaterialType = request.RequiredMaterialType ?? gcode.RequiredMaterial,
            RequiredPrinterModel = request.RequiredPrinterModel ?? gcode.PrinterModel?.Name ?? gcode.ExtractedPrinterModelName
        };

        Guid? assignedPrinterId = effectiveRequest.AssignedPrinterId;
        if (assignedPrinterId is null)
        {
            assignedPrinterId = await FindBestAvailablePrinterAsync(effectiveRequest, ct);

            if (assignedPrinterId is null)
            {
                _logger.LogInformation(
                    "No compatible printer found for job. Model={Model}, Material={Material}, Nozzle={Nozzle}",
                    effectiveRequest.RequiredPrinterModel ?? "(any)",
                    effectiveRequest.RequiredMaterialType ?? "(any)",
                    effectiveRequest.RequiredNozzleDiameter?.ToString("F2") ?? "(any)");
                return null;
            }
        }

        PrintJob job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = gcode.Name,
            GcodeFileId = request.GcodeFileId,
            AssignedPrinterId = assignedPrinterId,
            Status = PrintJobStatus.Queued,
            Priority = (int)request.Priority,
            QueuePosition = await _dataService.GetNextQueuePositionAsync(assignedPrinterId.Value, ct),
            RequiredNozzleDiameter = request.RequiredNozzleDiameter,
            RequiredMaterialType = request.RequiredMaterialType,
            EstimatedPrintTime = gcode.EstimatedPrintTimeMinutes.HasValue ? TimeSpan.FromMinutes(gcode.EstimatedPrintTimeMinutes.Value) : null,
            EstimatedFilamentUsage = gcode.EstimatedFilamentWeightG,
            ProjectId = request.ProjectId,
            ProjectName = request.ProjectName,
            SpoolmanFilamentId = request.SpoolmanFilamentId,
            FilamentName = request.FilamentName,
            FilamentVendor = request.FilamentVendor,
            FilamentColor = request.FilamentColor,
            Copies = request.Copies,
            ProjectFileId = request.ProjectFileId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow
        };

        // Calculate estimated cost if cost calculator is available
        if (_costCalculator != null && job.SpoolmanFilamentId.HasValue)
        {
            try
            {
                job.EstimatedCost = await _costCalculator.CalculateEstimatedCostAsync(
                    job.SpoolmanFilamentId,
                    job.EstimatedFilamentUsage,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to calculate estimated cost for queued job");
            }
        }

        await _repo.AddAsync(job, ct);
        await _repo.SaveChangesAsync(ct);

        // Notify auto-dispatch that a new job was queued for this printer.
        // This triggers immediate dispatch (skipping idle threshold) if the
        // printer is available and auto-dispatch is enabled.
        if (assignedPrinterId.HasValue)
        {
            _dispatchTrigger?.NotifyJobQueued(assignedPrinterId.Value);

            // Trigger the auto-dispatch ready gate if the printer is idle with
            // automatic dispatch enabled. This prompts the operator to confirm the bed
            // is clear before the job is dispatched — critical for first-time
            // uploads where no prior completion event exists to trigger PendingReady.
            if (_autoDispatchService is not null)
            {
                try
                {
                    await _autoDispatchService.TransitionToPendingReadyAsync(assignedPrinterId.Value, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to trigger auto-dispatch PendingReady for printer {PrinterId} after job queue",
                        assignedPrinterId.Value);
                }
            }
        }

        return new JobQueuePrintJobDto
        {
            Id = job.Id,
            GcodeFileId = job.GcodeFileId,
            GcodeFileName = gcode.Name,
            AssignedPrinterId = job.AssignedPrinterId,
            AssignedPrinterName = (await _dataService.GetAvailablePrintersAsync(ct)).Find(p => p.Id == assignedPrinterId)?.Name ?? "Unknown",
            Status = (PrintJobStatus?)job.Status,
            Priority = job.Priority,
            QueuePosition = job.QueuePosition,
            RequiredNozzleDiameter = job.RequiredNozzleDiameter,
            RequiredMaterialType = job.RequiredMaterialType,
            EstimatedPrintTime = job.EstimatedPrintTime,
            EstimatedFilamentUsage = job.EstimatedFilamentUsage,
            SpoolmanFilamentId = job.SpoolmanFilamentId,
            FilamentName = job.FilamentName,
            FilamentVendor = job.FilamentVendor,
            FilamentColor = job.FilamentColor,
            EstimatedCost = job.EstimatedCost,
            ActualCost = job.ActualCost,
            Copies = job.Copies,
            CompletedCopies = job.CompletedCopies,
            RemainingCopies = job.RemainingCopies,
            ProjectFileId = job.ProjectFileId,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            ToolheadUsages = MapToolheadUsages(job)
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
                GcodeFileName = job.GcodeFile?.Name ?? string.Empty,
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
                SpoolmanFilamentId = job.SpoolmanFilamentId,
                FilamentName = job.FilamentName,
                FilamentVendor = job.FilamentVendor,
                FilamentColor = job.FilamentColor,
                EstimatedCost = job.EstimatedCost,
                ActualCost = job.ActualCost,
                Copies = job.Copies,
                CompletedCopies = job.CompletedCopies,
                RemainingCopies = job.RemainingCopies,
                ProjectFileId = job.ProjectFileId,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.UpdatedAt,
                ToolheadUsages = MapToolheadUsages(job)
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
            GcodeFileName = job.GcodeFile?.Name ?? string.Empty,
            AssignedPrinterId = job.AssignedPrinterId,
            AssignedPrinterName = job.AssignedPrinter?.Name ?? "Unknown",
            Status = (PrintJobStatus?)job.Status,
            Priority = job.Priority,
            QueuePosition = job.QueuePosition,
            EstimatedPrintTime = job.EstimatedPrintTime,
            EstimatedFilamentUsage = job.EstimatedFilamentUsage,
            EstimatedCost = job.EstimatedCost,
            ActualCost = job.ActualCost,
            Copies = job.Copies,
            CompletedCopies = job.CompletedCopies,
            RemainingCopies = job.RemainingCopies,
            ProjectFileId = job.ProjectFileId,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            ToolheadUsages = MapToolheadUsages(job)
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

        if (!string.IsNullOrEmpty(request.Name))
        {
            job.Name = request.Name;
        }

        // Filament assignment (0 = clear)
        if (request.SpoolmanFilamentId.HasValue)
        {
            if (request.SpoolmanFilamentId.Value == 0)
            {
                job.SpoolmanFilamentId = null;
                job.FilamentName = null;
                job.FilamentVendor = null;
                job.FilamentColor = null;
            }
            else
            {
                job.SpoolmanFilamentId = request.SpoolmanFilamentId.Value;
                job.FilamentName = request.FilamentName;
                job.FilamentVendor = request.FilamentVendor;
                job.FilamentColor = request.FilamentColor;
            }
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
            GcodeFileName = job.GcodeFile?.Name ?? string.Empty,
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
            SpoolmanFilamentId = job.SpoolmanFilamentId,
            FilamentName = job.FilamentName,
            FilamentVendor = job.FilamentVendor,
            FilamentColor = job.FilamentColor,
            EstimatedCost = job.EstimatedCost,
            ActualCost = job.ActualCost,
            Copies = job.Copies,
            CompletedCopies = job.CompletedCopies,
            RemainingCopies = job.RemainingCopies,
            ProjectFileId = job.ProjectFileId,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            ToolheadUsages = MapToolheadUsages(job!)
        };
    }

    private async Task<Guid?> FindBestAvailablePrinterAsync(QueuePrintJobDto request, CancellationToken ct)
    {
        // Filter by model if specified (same logic as manual printer selection)
        List<Printer> printers = string.IsNullOrWhiteSpace(request.RequiredPrinterModel)
            ? await _dataService.GetAvailablePrintersAsync(ct)
            : await _dataService.GetCompatiblePrintersAsync(request.RequiredPrinterModel, ct);

        foreach (Printer printer in printers)
        {
            // Check nozzle diameter - now per-toolhead, check if any toolhead's nozzle model matches
            if (request.RequiredNozzleDiameter.HasValue)
            {
                double requiredDiameter = (double)request.RequiredNozzleDiameter;
                bool hasCompatibleToolhead = printer.Toolheads?.Any(t => t.NozzleModel != null && Math.Abs(t.NozzleModel.Diameter - requiredDiameter) <= 0.01) ?? false;
                if (!hasCompatibleToolhead)
                {
                    continue;
                }
            }

            // Check supported materials - case-insensitive comparison for material matching
            if (!string.IsNullOrEmpty(request.RequiredMaterialType))
            {
                bool hasCompatibleToolhead = printer.Toolheads?.Any(t =>
                    t.SupportedMaterials?.Any(m => string.Equals(m, request.RequiredMaterialType, StringComparison.OrdinalIgnoreCase)) ?? false) ?? false;
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

        totalMinutes += queuedJobs
            .Where(j => j.EstimatedPrintTime.HasValue && j != currentJob)
            .Sum(j => j.EstimatedPrintTime!.Value.TotalMinutes);

        return totalMinutes > 0 ? DateTime.UtcNow.AddMinutes(totalMinutes) : null;
    }

    private static List<PrintJobToolheadUsageDto> MapToolheadUsages(PrintJob job) =>
        (job.ToolheadUsages ?? [])
            .OrderBy(tu => tu.ToolheadIndex)
            .Select(tu => new PrintJobToolheadUsageDto(
                tu.Id,
                tu.PrintJobId,
                tu.ToolheadIndex,
                tu.SpoolmanSpoolId,
                tu.FilamentUsageGrams,
                tu.FilamentName,
                tu.FilamentColor,
                tu.MaterialCostUsd))
            .ToList();
}
