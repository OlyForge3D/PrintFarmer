using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Farm.Web.Api.Repositories.Queue;
using Farm.Infrastructure.Repositories.Printers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages the print job queue and printer assignment
/// </summary>
[ApiController]
[Route("api/job-queue")]
[Tags("Print Job Queue")]
public class JobQueueController(IQueueRepository queueRepo, IPrintersRepository printersRepo, IUnifiedLoggingService logger) : ControllerBase
{
    /// <summary>
    /// Get all jobs in the queue
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<JobQueuePrintJobDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<JobQueuePrintJobDto>>> GetQueueAsync()
    {
        try
        {
            List<PrintJob> jobs = await queueRepo.GetAllPrintJobsAsync(CancellationToken.None) ?? new List<PrintJob>();

            return Ok(jobs.Select(job => new JobQueuePrintJobDto
            {
                Id = job.Id,
                GcodeFileId = job.GcodeFileId,
                GcodeFileName = job.GcodeFile?.OriginalFileName ?? string.Empty,
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
            }));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving print job queue");
            return Problem("An error occurred while retrieving the queue", statusCode: 500);
        }
    }

    /// <summary>
    /// Add a new job to the queue
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<JobQueuePrintJobDto>> QueueJobAsync([FromBody] QueuePrintJobDto request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }
        try
        {
            // Validate the gcode file exists
            GcodeFile? gcodeFile = await queueRepo.GetGcodeFileAsync(request.GcodeFileId, CancellationToken.None);
            if (gcodeFile == null)
            {
                return NotFound($"G-code file with ID {request.GcodeFileId} not found");
            }

            // Create the job
            PrintJob job = new()
            {
                Id = Guid.NewGuid(),
                Name = gcodeFile.OriginalFileName,
                GcodeFileId = request.GcodeFileId,
                AssignedPrinterId = request.AssignedPrinterId,
                Status = PrintJobStatus.Queued,
                Priority = (int)request.Priority,
                RequiredNozzleDiameter = request.RequiredNozzleDiameter,
                RequiredMaterialType = request.RequiredMaterialType,
                EstimatedPrintTime = gcodeFile.EstimatedPrintTimeMinutes.HasValue ?
                    TimeSpan.FromMinutes(gcodeFile.EstimatedPrintTimeMinutes.Value) : null,
                EstimatedFilamentUsage = gcodeFile.EstimatedFilamentLengthMm,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };

            // Set queue position (global queued jobs)
            int maxPosition = await queueRepo.GetNextGlobalQueuePositionAsync(CancellationToken.None);
            job.QueuePosition = maxPosition;

            await queueRepo.AddPrintJobAsync(job, CancellationToken.None);
            await queueRepo.SaveChangesAsync(CancellationToken.None);

            // Load related entities for response via repo-loaded entities
            PrintJob? saved = await queueRepo.GetPrintJobByIdAsync(job.Id, CancellationToken.None);

            if (saved == null)
            {
                // This should not happen immediately after save, but guard for analysis
                return CreatedAtAction(nameof(GetJobAsync), new { id = job.Id }, new JobQueuePrintJobDto
                {
                    Id = job.Id,
                    GcodeFileId = job.GcodeFileId,
                    GcodeFileName = job.Name,
                    AssignedPrinterId = job.AssignedPrinterId,
                    AssignedPrinterName = string.Empty,
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
                });
            }

            return CreatedAtAction(nameof(GetJobAsync), new { id = job.Id }, new JobQueuePrintJobDto
            {
                Id = saved.Id,
                GcodeFileId = saved.GcodeFileId,
                GcodeFileName = saved.GcodeFile?.OriginalFileName ?? string.Empty,
                AssignedPrinterId = saved.AssignedPrinterId,
                AssignedPrinterName = saved.AssignedPrinter?.Name ?? string.Empty,
                Status = (PrintJobStatus?)saved.Status,
                Priority = saved.Priority,
                QueuePosition = saved.QueuePosition,
                RequiredNozzleDiameter = saved.RequiredNozzleDiameter,
                RequiredMaterialType = saved.RequiredMaterialType,
                EstimatedPrintTime = saved.EstimatedPrintTime,
                EstimatedFilamentUsage = saved.EstimatedFilamentUsage,
                ActualStartTime = saved.ActualStartTime,
                ActualEndTime = saved.ActualEndTime,
                ActualPrintTime = saved.ActualPrintTime,
                ActualFilamentUsage = saved.ActualFilamentUsage,
                FailureReason = saved.FailureReason,
                CreatedAt = saved.CreatedAt,
                UpdatedAt = saved.UpdatedAt
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error queueing print job for file {request.GcodeFileId}");
            return Problem("An error occurred while queueing the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Get a specific job
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<JobQueuePrintJobDto>> GetJobAsync(Guid id)
    {
        try
        {
            PrintJob? job = await queueRepo.GetPrintJobByIdAsync(id, CancellationToken.None);

            if (job == null)
            {
                return NotFound($"Print job with ID {id} not found");
            }

            return Ok(new JobQueuePrintJobDto
            {
                Id = job.Id,
                GcodeFileId = job.GcodeFileId,
                GcodeFileName = job.GcodeFile?.OriginalFileName ?? string.Empty,
                AssignedPrinterId = job.AssignedPrinterId,
                AssignedPrinterName = job.AssignedPrinter?.Name ?? string.Empty,
                Status = (PrintJobStatus?)(int)job.Status,
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
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error retrieving print job {id}");
            return Problem("An error occurred while retrieving the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Update job status, priority, or assignment
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<JobQueuePrintJobDto>> UpdateJobAsync(Guid id, [FromBody] UpdatePrintJobStatusDto request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }
        try
        {
            PrintJob? job = await queueRepo.GetPrintJobByIdAsync(id, CancellationToken.None);

            if (job == null)
            {
                return NotFound($"Print job with ID {id} not found");
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
                // Validate printer exists
                Printer? printer = await printersRepo.FindByIdAsync(request.AssignedPrinterId.Value, CancellationToken.None);
                if (printer == null)
                {
                    return BadRequest($"Printer with ID {request.AssignedPrinterId} not found");
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

            await queueRepo.SaveChangesAsync(CancellationToken.None);

            // Reload printer if assignment changed
            if (request.AssignedPrinterId.HasValue)
            {
                job = await queueRepo.GetPrintJobByIdAsync(id, CancellationToken.None);
            }

            return Ok(new JobQueuePrintJobDto
            {
                Id = job!.Id,
                GcodeFileId = job.GcodeFileId,
                GcodeFileName = job.GcodeFile?.OriginalFileName ?? string.Empty,
                AssignedPrinterId = job.AssignedPrinterId,
                AssignedPrinterName = job.AssignedPrinter?.Name ?? string.Empty,
                Status = (PrintJobStatus?)(int)job.Status,
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
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error updating print job {id}");
            return Problem("An error occurred while updating the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Delete a job from the queue
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DeleteJobAsync(Guid id)
    {
        try
        {
            PrintJob? job = await queueRepo.GetPrintJobByIdAsync(id, CancellationToken.None);
            if (job == null)
            {
                return NotFound($"Print job with ID {id} not found");
            }

            // Can only delete queued or failed jobs
            if (job.Status == PrintJobStatus.Printing || job.Status == PrintJobStatus.Starting)
            {
                return BadRequest("Cannot delete a job that is currently printing");
            }

            await queueRepo.RemovePrintJobAsync(job, CancellationToken.None);
            await queueRepo.SaveChangesAsync(CancellationToken.None);

            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error deleting print job {id}");
            return Problem("An error occurred while deleting the job", statusCode: 500);
        }
    }
}
