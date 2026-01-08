using Farm.Api.DTOs;
using Farm.Api.Services.Interfaces;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Api.Services.PrintQueue;

/// <summary>
/// Service for managing print queue operations
/// </summary>
public class PrintQueueService(
    AppDbContext dbContext,
    ILogger<PrintQueueService> logger
) : IPrintQueueService
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<PrintQueueService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // ============= QUERY OPERATIONS =============

    /// <summary>
    /// Get all queued and printing jobs with file metadata
    /// </summary>
    public async Task<List<QueuedPrintJobWithFileMetaDto>> GetAllQueuedJobsAsync(
        string? filterStatus = null,
        string? filterModel = null,
        string? filterMaterial = null,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var query = _dbContext.PrintJobs
                .Include(pj => pj.GcodeFile)
                .Include(pj => pj.AssignedPrinter)
                    .ThenInclude(p => p!.Model)
                .AsQueryable();

            // Filter by status
            if (!string.IsNullOrEmpty(filterStatus))
            {
                if (Enum.TryParse<PrintJobStatus>(filterStatus, ignoreCase: true, out var status))
                {
                    query = query.Where(pj => pj.Status == status);
                }
            }
            else
            {
                // Default: only show queued and printing jobs
                query = query.Where(pj => pj.Status == PrintJobStatus.Queued || pj.Status == PrintJobStatus.Printing);
            }

            // Filter by printer model
            if (!string.IsNullOrEmpty(filterModel))
            {
                query = query.Where(pj => pj.AssignedPrinter != null && 
                    pj.AssignedPrinter.Model != null &&
                    pj.AssignedPrinter.Model.Name.Contains(filterModel));
            }

            // Filter by material
            if (!string.IsNullOrEmpty(filterMaterial))
            {
                query = query.Where(pj => (pj.RequiredMaterialType != null && 
                    pj.RequiredMaterialType.Contains(filterMaterial)) ||
                    (pj.GcodeFile != null && pj.GcodeFile.RequiredMaterial != null &&
                    pj.GcodeFile.RequiredMaterial.Contains(filterMaterial)));
            }

            // Apply pagination
            var jobs = await query
                .OrderByDescending(pj => pj.Priority)
                .ThenBy(pj => pj.QueuePosition)
                .Skip(offset)
                .Take(limit)
                .ToListAsync(cancellationToken);

            return jobs.Select(pj => MapToQueuedPrintJobWithFileMeta(pj)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all queued jobs with filters: Status={FilterStatus}, Model={FilterModel}, Material={FilterMaterial}", 
                filterStatus, filterModel, filterMaterial);
            throw;
        }
    }

    /// <summary>
    /// Get print jobs for a specific printer
    /// </summary>
    public async Task<List<QueuedPrintJobDto>> GetPrinterQueueAsync(
        string printerId,
        int limit = 50,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var printerId_guid = Guid.Parse(printerId);
            var jobs = await _dbContext.PrintJobs
                .Where(pj => pj.AssignedPrinterId == printerId_guid &&
                    (pj.Status == PrintJobStatus.Queued || pj.Status == PrintJobStatus.Printing))
                .OrderByDescending(pj => pj.Priority)
                .ThenBy(pj => pj.QueuePosition)
                .Take(limit)
                .ToListAsync(cancellationToken);

            return jobs.Select(MapToQueuedPrintJobDto).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving printer queue for printer {PrinterId}", printerId);
            throw;
        }
    }

    /// <summary>
    /// Get aggregated queue statistics
    /// </summary>
    public async Task<QueueStatsDto> GetQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var allJobs = await _dbContext.PrintJobs.ToListAsync(cancellationToken);

            var stats = new QueueStatsDto
            {
                TotalQueued = allJobs.Count(j => j.Status == PrintJobStatus.Queued),
                TotalPrinting = allJobs.Count(j => j.Status == PrintJobStatus.Printing),
                TotalPaused = allJobs.Count(j => j.Status == PrintJobStatus.Paused),
                AverageWaitTimeMinutes = 0 // TODO: Calculate from queue entries
            };

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving queue statistics");
            throw;
        }
    }

    /// <summary>
    /// Get printer model statistics with queue counts
    /// </summary>
    public async Task<List<QueuePrinterModelStatsDto>> GetModelStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await _dbContext.PrintJobs
                .Include(pj => pj.AssignedPrinter)
                    .ThenInclude(p => p!.Model)
                .Where(pj => pj.AssignedPrinter != null && pj.AssignedPrinter.Model != null)
                .GroupBy(pj => pj.AssignedPrinter!.Model!.Name)
                .Select(g => new QueuePrinterModelStatsDto
                {
                    ModelName = g.Key,
                    TotalQueued = g.Count(j => j.Status == PrintJobStatus.Queued),
                    CurrentlyPrinting = g.Count(j => j.Status == PrintJobStatus.Printing),
                    OldestQueuedAtUtc = g.Where(j => j.Status == PrintJobStatus.Queued)
                        .Min(j => (DateTime?)j.QueuedAt),
                    AverageQueueWaitMinutes = 0 // TODO: Calculate from historical data
                })
                .ToListAsync(cancellationToken);

            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving printer model statistics");
            throw;
        }
    }

    /// <summary>
    /// Get print job history (Phase 2)
    /// </summary>
    public async Task<QueueHistoryPageDto> GetQueueHistoryAsync(
        int limit = 50,
        int offset = 0,
        string sortBy = "completedAt",
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var allCompletedJobs = await _dbContext.PrintJobs
                .Where(pj => pj.Status == PrintJobStatus.Completed || 
                            pj.Status == PrintJobStatus.Failed ||
                            pj.Status == PrintJobStatus.Cancelled)
                .ToListAsync(cancellationToken);

            var entries = allCompletedJobs
                .Select(pj => new QueueHistoryEntryDto
                {
                    Id = pj.Id.ToString(),
                    JobName = pj.Name,
                    PrinterName = pj.AssignedPrinter?.Name ?? "Unassigned",
                    Status = pj.Status.ToString(),
                    CompletionPercentage = pj.Status == PrintJobStatus.Completed ? 100 : 0,
                    StartedAtUtc = pj.ActualStartTime ?? pj.CreatedAt,
                    CompletedAtUtc = pj.ActualEndTime,
                    ActualPrintTimeSeconds = (int?)(pj.ActualPrintTime?.TotalSeconds) ?? 0,
                    FailureReason = pj.FailureReason
                })
                .OrderByDescending(e => e.CompletedAtUtc)
                .Skip(offset)
                .Take(limit)
                .ToList();

            return new QueueHistoryPageDto
            {
                Entries = entries,
                TotalCount = allCompletedJobs.Count,
                CurrentPage = offset / limit,
                PageSize = limit
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving queue history");
            throw;
        }
    }

    // ============= COMMAND OPERATIONS =============

    /// <summary>
    /// Enqueue a print job
    /// </summary>
    public async Task<QueuedPrintJobDto> EnqueueJobAsync(
        EnqueueQueueJobRequest request,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrEmpty(request.GcodeFileId))
                throw new ArgumentException("GcodeFileId is required");

            // Verify gcode file exists
            var gcodeFile = await _dbContext.GcodeFiles.FindAsync(new object[] { request.GcodeFileId }, cancellationToken);
            if (gcodeFile == null)
                throw new InvalidOperationException($"G-code file {request.GcodeFileId} not found");

            // Create new print job
            var job = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = gcodeFile.FileName,
                GcodeFileId = Guid.Parse(request.GcodeFileId),
                AssignedPrinterId = string.IsNullOrEmpty(request.AssignedPrinterId) ? null : Guid.Parse(request.AssignedPrinterId),
                Status = PrintJobStatus.Queued,
                Priority = request.Priority,
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

            // Calculate queue position
            var maxPosition = await _dbContext.PrintJobs
                .Where(pj => pj.Status == PrintJobStatus.Queued || pj.Status == PrintJobStatus.Printing)
                .MaxAsync(pj => (int?)pj.QueuePosition, cancellationToken) ?? -1;
            job.QueuePosition = maxPosition + 1;

            _dbContext.PrintJobs.Add(job);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Print job {JobId} enqueued by user {UserId}", job.Id.ToString(), userId);
            return MapToQueuedPrintJobDto(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enqueueing print job from gcode file {GcodeFileId}", request.GcodeFileId);
            throw;
        }
    }

    /// <summary>
    /// Update print job (status, priority, printer assignment)
    /// </summary>
    public async Task<QueuedPrintJobDto> UpdateJobAsync(
        string jobId,
        UpdateQueueJobRequest request,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var job = await _dbContext.PrintJobs.FindAsync(new object[] { jobId }, cancellationToken);
            if (job == null)
                throw new InvalidOperationException($"Print job {jobId} not found");

            // Update fields if provided
            if (request.Priority.HasValue)
                job.Priority = request.Priority.Value;

            if (!string.IsNullOrEmpty(request.AssignedPrinterId))
                job.AssignedPrinterId = Guid.Parse(request.AssignedPrinterId);

            if (!string.IsNullOrEmpty(request.Status))
            {
                if (Enum.TryParse<PrintJobStatus>(request.Status, ignoreCase: true, out var newStatus))
                    job.Status = newStatus;
            }

            if (!string.IsNullOrEmpty(request.FailureReason))
                job.FailureReason = request.FailureReason;

            job.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Print job {JobId} updated by user {UserId}", jobId, userId);

            return MapToQueuedPrintJobDto(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating print job {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Update job priority (for reordering queue)
    /// </summary>
    public async Task<QueuedPrintJobDto> UpdateJobPriorityAsync(
        string jobId,
        int newPriority,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var job = await _dbContext.PrintJobs.FindAsync(new object[] { jobId }, cancellationToken);
            if (job == null)
                throw new InvalidOperationException($"Print job {jobId} not found");

            job.Priority = newPriority;
            job.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Print job {JobId} priority updated to {Priority} by user {UserId}", jobId, newPriority, userId);

            return MapToQueuedPrintJobDto(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating priority for print job {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Pause a printing job
    /// </summary>
    public async Task<QueuedPrintJobDto> PauseJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var job = await _dbContext.PrintJobs.FindAsync(new object[] { jobId }, cancellationToken);
            if (job == null)
                throw new InvalidOperationException($"Print job {jobId} not found");

            if (job.Status != PrintJobStatus.Printing)
                throw new InvalidOperationException($"Only printing jobs can be paused. Current status: {job.Status}");

            job.Status = PrintJobStatus.Paused;
            job.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Print job {JobId} paused by user {UserId}", jobId, userId);

            return MapToQueuedPrintJobDto(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing print job {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Resume a paused job
    /// </summary>
    public async Task<QueuedPrintJobDto> ResumeJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var job = await _dbContext.PrintJobs.FindAsync(new object[] { jobId }, cancellationToken);
            if (job == null)
                throw new InvalidOperationException($"Print job {jobId} not found");

            if (job.Status != PrintJobStatus.Paused)
                throw new InvalidOperationException($"Only paused jobs can be resumed. Current status: {job.Status}");

            job.Status = PrintJobStatus.Printing;
            job.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Print job {JobId} resumed by user {UserId}", jobId, userId);

            return MapToQueuedPrintJobDto(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming print job {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Cancel a job (remove from queue or stop printing)
    /// </summary>
    public async Task CancelJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var job = await _dbContext.PrintJobs.FindAsync(new object[] { jobId }, cancellationToken);
            if (job == null)
                throw new InvalidOperationException($"Print job {jobId} not found");

            if (job.Status == PrintJobStatus.Completed || job.Status == PrintJobStatus.Cancelled)
                throw new InvalidOperationException($"Cannot cancel a {job.Status} job");

            job.Status = PrintJobStatus.Cancelled;
            job.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Print job {JobId} cancelled by user {UserId}", jobId, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling print job {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Cancel multiple jobs at once
    /// </summary>
    public async Task<QueueBulkOperationResultDto> BulkCancelJobsAsync(
        List<string> jobIds,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var result = new QueueBulkOperationResultDto
        {
            TotalRequested = jobIds.Count,
            SuccessfulCount = 0,
            FailedCount = 0,
            Failures = new(),
            CompletedAtUtc = DateTime.UtcNow
        };

        try
        {
            foreach (var jobId in jobIds)
            {
                try
                {
                    await CancelJobAsync(jobId, userId, cancellationToken);
                    result.SuccessfulCount++;
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    result.Failures.Add(new QueueOperationFailureDto
                    {
                        ItemId = jobId,
                        ErrorCode = "CANCEL_FAILED",
                        ErrorMessage = ex.Message
                    });
                }
            }

            _logger.LogInformation("Bulk cancel completed: {SuccessCount} succeeded, {FailureCount} failed", 
                result.SuccessfulCount, result.FailedCount);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in bulk cancel operation");
            throw;
        }
    }

    /// <summary>
    /// Reorder multiple jobs in queue
    /// </summary>
    public async Task<QueueBulkOperationResultDto> BulkReorderJobsAsync(
        List<QueueJobReorderMove> moves,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var result = new QueueBulkOperationResultDto
        {
            TotalRequested = moves.Count,
            SuccessfulCount = 0,
            FailedCount = 0,
            Failures = new(),
            CompletedAtUtc = DateTime.UtcNow
        };

        try
        {
            foreach (var move in moves)
            {
                try
                {
                    var job = await _dbContext.PrintJobs.FindAsync(new object[] { move.JobId }, cancellationToken);
                    if (job == null)
                        throw new InvalidOperationException($"Job {move.JobId} not found");

                    job.QueuePosition = move.NewPosition;
                    job.UpdatedAt = DateTime.UtcNow;
                    result.SuccessfulCount++;
                }
                catch (Exception ex)
                {
                    result.FailedCount++;
                    result.Failures.Add(new QueueOperationFailureDto
                    {
                        ItemId = move.JobId,
                        ErrorCode = "REORDER_FAILED",
                        ErrorMessage = ex.Message
                    });
                }
            }

            if (result.SuccessfulCount > 0)
                await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Bulk reorder completed: {SuccessCount} succeeded, {FailureCount} failed", 
                result.SuccessfulCount, result.FailedCount);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in bulk reorder operation");
            throw;
        }
    }

    /// <summary>
    /// Rerun a completed job (add it back to queue)
    /// </summary>
    public async Task<QueuedPrintJobDto> RerunJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
                throw new ArgumentException("Job ID is required");

            // Find the job to rerun
            var originalJob = await _dbContext.PrintJobs.FirstOrDefaultAsync(j => j.Id == Guid.Parse(jobId), cancellationToken)
                ?? throw new InvalidOperationException($"Job {jobId} not found");

            // Create new print job with same properties as original
            var newJob = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = originalJob.Name,
                GcodeFileId = originalJob.GcodeFileId,
                AssignedPrinterId = originalJob.AssignedPrinterId,
                Status = PrintJobStatus.Queued,
                Priority = originalJob.Priority,
                RequiredNozzleDiameter = originalJob.RequiredNozzleDiameter,
                RequiredMaterialType = originalJob.RequiredMaterialType,
                RequiredCapabilities = originalJob.RequiredCapabilities,
                EstimatedPrintTime = originalJob.EstimatedPrintTime,
                EstimatedFilamentUsage = originalJob.EstimatedFilamentUsage,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };

            // Calculate queue position
            var maxPosition = await _dbContext.PrintJobs
                .Where(pj => pj.Status == PrintJobStatus.Queued || pj.Status == PrintJobStatus.Printing)
                .MaxAsync(pj => (int?)pj.QueuePosition, cancellationToken) ?? -1;
            newJob.QueuePosition = maxPosition + 1;

            _dbContext.PrintJobs.Add(newJob);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Job {JobId} rerun as {NewJobId} by user {UserId}",
                originalJob.Id, newJob.Id, userId
            );

            return MapToQueuedPrintJobDto(newJob);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rerunning job {JobId}", jobId);
            throw;
        }
    }

    // ============= HISTORY OPERATIONS (Phase 2) =============

    /// <summary>
    /// Seed print job history from printer history (Phase 2)
    /// </summary>
    public async Task SeedHistoryFromPrintersAsync(
        List<string>? printerIds = null,
        int daysBack = 30,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogInformation("Seeding history from printers for last {DaysBack} days", daysBack);
            // TODO: Implement in Phase 2 when PrintJobHistory table is added
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding queue history");
            throw;
        }
    }

    // ============= PRIVATE HELPERS =============

    private QueuedPrintJobWithFileMetaDto MapToQueuedPrintJobWithFileMeta(PrintJob job)
    {
        return new QueuedPrintJobWithFileMetaDto
        {
            Job = MapToQueuedPrintJobDto(job),
            GcodeFile = job.GcodeFile != null ? MapToQueueGcodeFileMetaDto(job.GcodeFile) : new QueueGcodeFileMetaDto { FileName = "Unknown" },
            AssignedPrinter = job.AssignedPrinter != null ? MapToQueuePrinterMetaDto(job.AssignedPrinter) : null,
            EstimatedStartTime = null, // TODO: Calculate based on queue position and estimated times
            EstimatedCompletionTime = null // TODO: Calculate based on estimated print time
        };
    }

    private QueuedPrintJobDto MapToQueuedPrintJobDto(PrintJob job)
    {
        return new QueuedPrintJobDto
        {
            Id = job.Id.ToString(),
            Name = job.Name,
            GcodeFileId = job.GcodeFileId.ToString(),
            AssignedPrinterId = job.AssignedPrinterId?.ToString(),
            Status = job.Status.ToString(),
            Priority = job.Priority,
            QueuePosition = job.QueuePosition,
            RequiredNozzleDiameter = job.RequiredNozzleDiameter,
            RequiredMaterialType = job.RequiredMaterialType,
            RequiredCapabilities = job.RequiredCapabilities,
            EstimatedPrintTimeSeconds = (int?)job.EstimatedPrintTime?.TotalSeconds,
            EstimatedFilamentUsageGrams = (int?)job.EstimatedFilamentUsage,
            ActualStartTimeUtc = job.ActualStartTime,
            ActualEndTimeUtc = job.ActualEndTime,
            ActualPrintTimeSeconds = (int?)job.ActualPrintTime?.TotalSeconds,
            ActualFilamentUsageGrams = (int?)job.ActualFilamentUsage,
            FailureReason = job.FailureReason,
            CreatedAtUtc = job.CreatedAt,
            UpdatedAtUtc = job.UpdatedAt,
            QueuedAtUtc = job.QueuedAt
        };
    }

    private QueueGcodeFileMetaDto MapToQueueGcodeFileMetaDto(GcodeFile file)
    {
        return new QueueGcodeFileMetaDto
        {
            Id = file.Id.ToString(),
            FileName = file.FileName,
            FileSizeBytes = file.FileSizeBytes,
            MaterialType = file.RequiredMaterial,
            NozzleDiameter = (int?)file.RequiredNozzleDiameter,
            EstimatedPrintTimeSeconds = (int?)(file.EstimatedPrintTimeMinutes.HasValue ? file.EstimatedPrintTimeMinutes * 60 : null),
            EstimatedFilamentUsageGrams = (int?)file.EstimatedFilamentWeightG,
            CreatedAtUtc = file.CreatedAt
        };
    }

    private QueuePrinterMetaDto MapToQueuePrinterMetaDto(Printer printer)
    {
        return new QueuePrinterMetaDto
        {
            Id = printer.Id.ToString(),
            Name = printer.Name,
            ModelName = printer.Model?.Name ?? "Unknown",
            Status = "Online", // TODO: Get actual printer status
            IsOnline = true // TODO: Get actual online status
        };
    }

    // ============= JOB DETAILS OPERATIONS (Phase 3) =============

    /// <summary>
    /// Get detailed information about a specific job
    /// </summary>
    public async Task<QueuedPrintJobDto?> GetJobByIdAsync(
        string jobId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return null;

            var job = await _dbContext.PrintJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(pj => pj.Id.ToString() == jobId, cancellationToken);

            return job != null ? MapToQueuedPrintJobDto(job) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving job details for {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Update job details (name, priority, notes, tags, material, nozzle)
    /// </summary>
    public async Task<QueuedPrintJobDto?> UpdateJobDetailsAsync(
        string jobId,
        UpdateJobDetailsRequest updates,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
                throw new ArgumentException("Job ID is required", nameof(jobId));

            if (updates == null)
                throw new ArgumentNullException(nameof(updates), "Update data is required");

            var job = await _dbContext.PrintJobs
                .FirstOrDefaultAsync(pj => pj.Id.ToString() == jobId, cancellationToken);

            if (job == null)
                return null;

            // Validate and update fields
            if (!string.IsNullOrEmpty(updates.Name))
            {
                if (updates.Name.Length > 255)
                    throw new ArgumentException("Job name must be 255 characters or less", nameof(updates.Name));
                job.Name = updates.Name;
            }

            if (updates.Priority.HasValue)
            {
                if (updates.Priority < 0 || updates.Priority > 100)
                    throw new ArgumentException("Priority must be between 0 and 100", nameof(updates.Priority));
                job.Priority = updates.Priority.Value;
            }

            if (updates.Notes != null)
            {
                if (updates.Notes.Length > 500)
                    throw new ArgumentException("Notes must be 500 characters or less", nameof(updates.Notes));
                job.Notes = updates.Notes;
            }

            if (updates.RequiredMaterialType != null)
            {
                job.RequiredMaterialType = updates.RequiredMaterialType;
            }

            if (updates.RequiredNozzleDiameter.HasValue)
            {
                job.RequiredNozzleDiameter = updates.RequiredNozzleDiameter;
            }

            // Handle tags (future phase enhancement)
            if (updates.Tags != null)
            {
                // TODO: Implement tag support in Phase 3D
                _logger.LogDebug("Tags update requested but not yet implemented for job {JobId}", jobId);
            }

            job.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Job {JobId} details updated: Name={Name}, Priority={Priority}, Notes={NotesLength}",
                jobId, job.Name, job.Priority, job.Notes?.Length ?? 0);

            return MapToQueuedPrintJobDto(job);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating job details for {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Update job notes
    /// </summary>
    public async Task<bool> UpdateJobNotesAsync(
        string jobId,
        string? notes,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
                throw new ArgumentException("Job ID is required", nameof(jobId));

            if (notes != null && notes.Length > 500)
                throw new ArgumentException("Notes must be 500 characters or less", nameof(notes));

            var job = await _dbContext.PrintJobs
                .FirstOrDefaultAsync(pj => pj.Id.ToString() == jobId, cancellationToken);

            if (job == null)
                return false;

            job.Notes = notes;
            job.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Notes updated for job {JobId}", jobId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating notes for job {JobId}", jobId);
            throw;
        }
    }
}

