using Farm.Web.Shared;
using Farm.Web.Api.Data;
using Farm.Web.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for managing print job queues and job assignment
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Tags("Job Queue Management")]
public class QueueController : ControllerBase
{
    private readonly ILogger<QueueController> _logger;
    private readonly AppDbContext _context;

    public QueueController(ILogger<QueueController> logger, AppDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    /// <summary>
    /// Get all printer queues with current jobs
    /// </summary>
    /// <returns>List of printer queues with job counts and status</returns>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(IEnumerable<QueueOverviewDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueueOverviewAsync()
    {
        try
        {
            var printers = await _context.Printers
                .Include(p => p.Model)
                .Include(p => p.Capabilities)
                .Where(p => p.Capabilities != null && p.Capabilities.IsAvailable)
                .ToListAsync();

            var queueOverview = new List<QueueOverviewDto>();

            foreach (var printer in printers)
            {
                var queuedJobs = await _context.PrintJobs
                    .Where(j => j.AssignedPrinterId == printer.Id && 
                               (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned))
                    .OrderBy(j => j.Priority)
                    .ThenBy(j => j.QueuedAt)
                    .ToListAsync();

                var currentJob = await _context.PrintJobs
                    .FirstOrDefaultAsync(j => j.AssignedPrinterId == printer.Id && 
                                            (j.Status == PrintJobStatus.Starting || j.Status == PrintJobStatus.Printing));

                queueOverview.Add(new QueueOverviewDto
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

            return Ok(queueOverview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue overview");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get queue overview");
        }
    }

    /// <summary>
    /// Get jobs in a specific printer's queue
    /// </summary>
    /// <param name="printerId">Printer ID</param>
    /// <returns>List of jobs in the queue</returns>
    [HttpGet("printer/{printerId:guid}")]
    [ProducesResponseType(typeof(IEnumerable<JobQueuePrintJobDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPrinterQueueAsync(Guid printerId)
    {
        try
        {
            var jobs = await _context.PrintJobs
                .Include(j => j.GcodeFile)
                .Include(j => j.AssignedPrinter)
                .Where(j => j.AssignedPrinterId == printerId)
                .OrderBy(j => j.Status == PrintJobStatus.Printing || j.Status == PrintJobStatus.Starting ? 0 : 1)
                .ThenBy(j => j.Priority)
                .ThenBy(j => j.QueuedAt)
                .Select(j => new JobQueuePrintJobDto
                {
                    Id = j.Id,
                    GcodeFileId = j.GcodeFileId,
                    GcodeFileName = j.GcodeFile.DisplayName,
                    AssignedPrinterId = j.AssignedPrinterId,
                    AssignedPrinterName = j.AssignedPrinter!.Name,
                    Status = (PrintJobStatusDto)j.Status,
                    Priority = j.Priority,
                    QueuePosition = 0, // Will be calculated below
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
                    UpdatedAt = j.UpdatedAt
                })
                .ToListAsync();

            // Calculate queue positions for queued jobs
            var queuedJobs = jobs.Where(j => j.Status == PrintJobStatusDto.Queued || j.Status == PrintJobStatusDto.Assigned).ToList();
            for (int i = 0; i < queuedJobs.Count; i++)
            {
                queuedJobs[i].QueuePosition = i + 1;
            }

            return Ok(jobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get printer queue for printer {PrinterId}", printerId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get printer queue");
        }
    }

    /// <summary>
    /// Add a job to a printer queue or auto-assign to best available printer
    /// </summary>
    /// <param name="request">Job queue request</param>
    /// <returns>Created job information</returns>
    [HttpPost("jobs")]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddJobToQueueAsync([FromBody] QueuePrintJobDto request)
    {
        if (request == null)
        {
            return BadRequest("Job request is required");
        }

        try
        {
            // Verify G-code file exists
            var gcodeFile = await _context.GcodeFiles.FindAsync(request.GcodeFileId);
            if (gcodeFile == null)
            {
                return BadRequest("G-code file not found");
            }

            // Auto-assign printer if not specified
            Guid? assignedPrinterId = request.AssignedPrinterId;
            if (assignedPrinterId == null)
            {
                assignedPrinterId = await FindBestAvailablePrinterAsync(request);
                if (assignedPrinterId == null)
                {
                    return BadRequest("No compatible printer available");
                }
            }

            // Create print job
            var printJob = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = gcodeFile.DisplayName,
                GcodeFileId = request.GcodeFileId,
                AssignedPrinterId = assignedPrinterId,
                Status = PrintJobStatus.Queued,
                Priority = (int)request.Priority,
                QueuePosition = await GetNextQueuePositionAsync(assignedPrinterId.Value),
                RequiredNozzleDiameter = request.RequiredNozzleDiameter,
                RequiredMaterialType = request.RequiredMaterialType,
                EstimatedPrintTime = gcodeFile.EstimatedPrintTimeMinutes.HasValue 
                    ? TimeSpan.FromMinutes(gcodeFile.EstimatedPrintTimeMinutes.Value) 
                    : null,
                EstimatedFilamentUsage = gcodeFile.EstimatedFilamentWeightG,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };

            _context.PrintJobs.Add(printJob);
            await _context.SaveChangesAsync();

            // Return job information
            var result = new JobQueuePrintJobDto
            {
                Id = printJob.Id,
                GcodeFileId = printJob.GcodeFileId,
                GcodeFileName = gcodeFile.DisplayName,
                AssignedPrinterId = printJob.AssignedPrinterId,
                AssignedPrinterName = (await _context.Printers.FindAsync(printJob.AssignedPrinterId))?.Name ?? "Unknown",
                Status = (PrintJobStatusDto)printJob.Status,
                Priority = printJob.Priority,
                QueuePosition = printJob.QueuePosition,
                RequiredNozzleDiameter = printJob.RequiredNozzleDiameter,
                RequiredMaterialType = printJob.RequiredMaterialType,
                EstimatedPrintTime = printJob.EstimatedPrintTime,
                EstimatedFilamentUsage = printJob.EstimatedFilamentUsage,
                CreatedAt = printJob.CreatedAt,
                UpdatedAt = printJob.UpdatedAt
            };

            _logger.LogInformation("Job added to queue: {JobId} for printer {PrinterId}", printJob.Id, assignedPrinterId);
            return Created($"/api/queue/jobs/{printJob.Id}", result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add job to queue");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to add job to queue");
        }
    }

    /// <summary>
    /// Get a specific job
    /// </summary>
    /// <param name="id">Job ID</param>
    /// <returns>Job information</returns>
    [HttpGet("jobs/{id:guid}")]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobAsync(Guid id)
    {
        var job = await _context.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job == null)
        {
            return NotFound();
        }

        var result = new JobQueuePrintJobDto
        {
            Id = job.Id,
            GcodeFileId = job.GcodeFileId,
            GcodeFileName = job.GcodeFile.DisplayName,
            AssignedPrinterId = job.AssignedPrinterId,
            AssignedPrinterName = job.AssignedPrinter?.Name ?? "Unknown",
            Status = (PrintJobStatusDto)job.Status,
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

        return Ok(result);
    }

    /// <summary>
    /// Remove a job from the queue
    /// </summary>
    /// <param name="id">Job ID</param>
    /// <returns>No content if successful</returns>
    [HttpDelete("jobs/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RemoveJobFromQueueAsync(Guid id)
    {
        var job = await _context.PrintJobs.FindAsync(id);
        if (job == null)
        {
            return NotFound();
        }

        // Can only remove queued or assigned jobs
        if (job.Status != PrintJobStatus.Queued && job.Status != PrintJobStatus.Assigned)
        {
            return BadRequest("Cannot remove job that is already started");
        }

        try
        {
            _context.PrintJobs.Remove(job);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Job removed from queue: {JobId}", id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove job from queue: {JobId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to remove job from queue");
        }
    }

    /// <summary>
    /// Update job priority
    /// </summary>
    /// <param name="id">Job ID</param>
    /// <param name="request">Priority update request</param>
    /// <returns>Updated job information</returns>
    [HttpPatch("jobs/{id:guid}/priority")]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateJobPriorityAsync(Guid id, [FromBody] UpdateJobPriorityDto request)
    {
        var job = await _context.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job == null)
        {
            return NotFound();
        }

        try
        {
            job.Priority = request.Priority;
            job.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            var result = new JobQueuePrintJobDto
            {
                Id = job.Id,
                GcodeFileId = job.GcodeFileId,
                GcodeFileName = job.GcodeFile.DisplayName,
                AssignedPrinterId = job.AssignedPrinterId,
                AssignedPrinterName = job.AssignedPrinter?.Name ?? "Unknown",
                Status = (PrintJobStatusDto)job.Status,
                Priority = job.Priority,
                QueuePosition = job.QueuePosition,
                RequiredNozzleDiameter = job.RequiredNozzleDiameter,
                RequiredMaterialType = job.RequiredMaterialType,
                EstimatedPrintTime = job.EstimatedPrintTime,
                EstimatedFilamentUsage = job.EstimatedFilamentUsage,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.UpdatedAt
            };

            _logger.LogInformation("Job priority updated: {JobId} to {Priority}", id, request.Priority);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update job priority: {JobId}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to update job priority");
        }
    }

    private async Task<Guid?> FindBestAvailablePrinterAsync(QueuePrintJobDto request)
    {
        var printers = await _context.Printers
            .Include(p => p.Capabilities)
            .Where(p => p.Capabilities != null && p.Capabilities.IsAvailable)
            .ToListAsync();

        foreach (var printer in printers)
        {
            // Check nozzle diameter compatibility
            if (request.RequiredNozzleDiameter.HasValue && 
                printer.Capabilities?.NozzleDiameter.HasValue == true &&
                Math.Abs(printer.Capabilities.NozzleDiameter.Value - (double)request.RequiredNozzleDiameter.Value) > 0.01)
            {
                continue;
            }

            // Check material compatibility
            if (!string.IsNullOrEmpty(request.RequiredMaterialType) &&
                printer.Capabilities?.SupportedMaterials != null &&
                !printer.Capabilities.SupportedMaterials.Contains(request.RequiredMaterialType))
            {
                continue;
            }

            // Check current queue load
            var queueCount = await _context.PrintJobs
                .CountAsync(j => j.AssignedPrinterId == printer.Id && 
                               (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned));

            // Simple load balancing - prefer printers with fewer queued jobs
            if (queueCount < 5) // Arbitrary limit
            {
                return printer.Id;
            }
        }

        return null;
    }

    private async Task<int> GetNextQueuePositionAsync(Guid printerId)
    {
        var maxPosition = await _context.PrintJobs
            .Where(j => j.AssignedPrinterId == printerId && 
                       (j.Status == PrintJobStatus.Queued || j.Status == PrintJobStatus.Assigned))
            .MaxAsync(j => (int?)j.QueuePosition) ?? 0;

        return maxPosition + 1;
    }

    private static DateTime? CalculateEstimatedCompletionTime(List<PrintJob> queuedJobs, PrintJob? currentJob)
    {
        var totalMinutes = 0.0;

        if (currentJob?.EstimatedPrintTime.HasValue == true)
        {
            var elapsed = currentJob.ActualStartTime.HasValue 
                ? DateTime.UtcNow - currentJob.ActualStartTime.Value
                : TimeSpan.Zero;
            var remaining = currentJob.EstimatedPrintTime.Value - elapsed;
            totalMinutes += Math.Max(0, remaining.TotalMinutes);
        }

        totalMinutes += queuedJobs
            .Where(j => j.EstimatedPrintTime.HasValue)
            .Sum(j => j.EstimatedPrintTime!.Value.TotalMinutes);

        return totalMinutes > 0 ? DateTime.UtcNow.AddMinutes(totalMinutes) : null;
    }
}