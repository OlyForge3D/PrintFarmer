using Farm.Api.DTOs;
using Farm.Api.Services.Interfaces;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Farm.Api.Services.PrintQueue;

/// <summary>
/// Service for managing print queue operations
/// </summary>
public class PrintQueueService(
    AppDbContext dbContext,
    ILogger<PrintQueueService> logger,
    INotificationService? notificationService = null
) : IPrintQueueService
{
    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ILogger<PrintQueueService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly INotificationService? _notificationService = notificationService;

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
            {
                throw new ArgumentException("GcodeFileId is required");
            }

            // Verify gcode file exists
            var gcodeFile = await _dbContext.GcodeFiles.FindAsync(new object[] { request.GcodeFileId }, cancellationToken);
            if (gcodeFile == null)
            {
                throw new InvalidOperationException($"G-code file {request.GcodeFileId} not found");
            }

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
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            // Update fields if provided
            if (request.Priority.HasValue)
            {
                job.Priority = request.Priority.Value;
            }

            if (!string.IsNullOrEmpty(request.AssignedPrinterId))
            {
                job.AssignedPrinterId = Guid.Parse(request.AssignedPrinterId);
            }

            if (!string.IsNullOrEmpty(request.Status))
            {
                if (Enum.TryParse<PrintJobStatus>(request.Status, ignoreCase: true, out var newStatus))
                {
                    job.Status = newStatus;
                }
            }

            if (!string.IsNullOrEmpty(request.FailureReason))
            {
                job.FailureReason = request.FailureReason;
            }

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
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

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
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            if (job.Status != PrintJobStatus.Printing)
            {
                throw new InvalidOperationException($"Only printing jobs can be paused. Current status: {job.Status}");
            }

            job.Status = PrintJobStatus.Paused;
            job.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Print job {JobId} paused by user {UserId}", jobId, userId);

            // Send notification
            await SendJobPauseNotificationAsync(job, "Job paused by user", cancellationToken);

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
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            if (job.Status != PrintJobStatus.Paused)
            {
                throw new InvalidOperationException($"Only paused jobs can be resumed. Current status: {job.Status}");
            }

            job.Status = PrintJobStatus.Printing;
            job.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Print job {JobId} resumed by user {UserId}", jobId, userId);

            // Send notification
            await SendJobResumeNotificationAsync(job, cancellationToken);

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
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            if (job.Status == PrintJobStatus.Completed || job.Status == PrintJobStatus.Cancelled)
            {
                throw new InvalidOperationException($"Cannot cancel a {job.Status} job");
            }

            job.Status = PrintJobStatus.Cancelled;
            job.UpdatedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Print job {JobId} cancelled by user {UserId}", jobId, userId);

            // Send notification
            await SendJobFailureNotificationAsync(job, "Job cancelled by user", cancellationToken);
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
                    {
                        throw new InvalidOperationException($"Job {move.JobId} not found");
                    }

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
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

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
            {
                throw new ArgumentException("Job ID is required");
            }

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
            {
                return null;
            }

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
            {
                throw new ArgumentException("Job ID is required", nameof(jobId));
            }

            if (updates == null)
            {
                throw new ArgumentNullException(nameof(updates), "Update data is required");
            }

            var job = await _dbContext.PrintJobs
                .FirstOrDefaultAsync(pj => pj.Id.ToString() == jobId, cancellationToken);

            if (job == null)
            {
                return null;
            }

            // Validate and update fields
            if (!string.IsNullOrEmpty(updates.Name))
            {
                if (updates.Name.Length > 255)
                {
                    throw new ArgumentException("Job name must be 255 characters or less", nameof(updates));
                }

                job.Name = updates.Name;
            }

            if (updates.Priority.HasValue)
            {
                if (updates.Priority < 0 || updates.Priority > 100)
                {
                    throw new ArgumentException("Priority must be between 0 and 100", nameof(updates));
                }

                job.Priority = updates.Priority.Value;
            }

            if (updates.Notes != null)
            {
                if (updates.Notes.Length > 500)
                {
                    throw new ArgumentException("Notes must be 500 characters or less", nameof(updates));
                }

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
            {
                throw new ArgumentException("Job ID is required", nameof(jobId));
            }

            if (notes != null && notes.Length > 500)
            {
                throw new ArgumentException("Notes must be 500 characters or less", nameof(notes));
            }

            var job = await _dbContext.PrintJobs
                .FirstOrDefaultAsync(pj => pj.Id.ToString() == jobId, cancellationToken);

            if (job == null)
            {
                return false;
            }

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

    // ============= TIMELINE & ANALYTICS OPERATIONS (Phase 3C) =============

    /// <summary>
    /// Get timeline events for visualization with optional filtering
    /// </summary>
    public async Task<IEnumerable<TimelineEventDto>> GetTimelineAsync(
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        string? printerId = null,
        string? filterStatus = null,
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var query = _dbContext.PrintJobs
                .Include(pj => pj.AssignedPrinter)
                .AsQueryable();

            // Apply date filters
            if (dateFrom.HasValue)
            {
                query = query.Where(pj => pj.ActualStartTime >= dateFrom || pj.CreatedAt >= dateFrom);
            }
            if (dateTo.HasValue)
            {
                query = query.Where(pj => pj.ActualEndTime <= dateTo || pj.CreatedAt <= dateTo);
            }

            // Apply printer filter
            if (!string.IsNullOrEmpty(printerId))
            {
                query = query.Where(pj => pj.AssignedPrinterId.ToString() == printerId);
            }

            // Apply status filter
            if (!string.IsNullOrEmpty(filterStatus))
            {
                if (Enum.TryParse<PrintJobStatus>(filterStatus, ignoreCase: true, out var status))
                {
                    query = query.Where(pj => pj.Status == status);
                }
            }

            var jobs = await query
                .OrderByDescending(pj => pj.CreatedAt)
                .Take(limit)
                .ToListAsync(cancellationToken);

            var events = jobs.Select(job => new TimelineEventDto
            {
                JobId = job.Id.ToString(),
                JobName = job.Name,
                PrinterName = job.AssignedPrinter?.Name ?? "Unassigned",
                State = job.Status.ToString(),
                EnteredAtUtc = job.Status == PrintJobStatus.Queued ? job.CreatedAt : job.ActualStartTime ?? job.CreatedAt,
                ExitedAtUtc = job.Status == PrintJobStatus.Completed || job.Status == PrintJobStatus.Failed || job.Status == PrintJobStatus.Cancelled
                    ? job.ActualEndTime
                    : null,
                DurationSeconds = job.ActualPrintTime.HasValue ? (int)job.ActualPrintTime.Value.TotalSeconds : null,
                EstimatedDurationSeconds = job.EstimatedPrintTime.HasValue ? (int)job.EstimatedPrintTime.Value.TotalSeconds : null,
                VariancePercent = job.EstimatedPrintTime.HasValue && job.ActualPrintTime.HasValue
                    ? CalculateVariancePercent((int)job.EstimatedPrintTime.Value.TotalSeconds, (int)job.ActualPrintTime.Value.TotalSeconds)
                    : null
            }).ToList();

            _logger.LogInformation("Retrieved {Count} timeline events", events.Count);
            return events;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting timeline");
            throw;
        }
    }

    /// <summary>
    /// Get complete state history for a specific job
    /// </summary>
    public async Task<JobStateHistoryDto> GetJobStateHistoryAsync(
        string jobId,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Job ID is required", nameof(jobId));
            }

            var job = await _dbContext.PrintJobs
                .Include(pj => pj.StateHistory)
                .FirstOrDefaultAsync(pj => pj.Id.ToString() == jobId, cancellationToken);

            if (job == null)
            {
                throw new ArgumentException($"Job {jobId} not found", nameof(jobId));
            }

            // Build state transitions from job history
            var transitions = new List<StateTransitionDto>();

            // Add initial Queued state
            transitions.Add(new StateTransitionDto
            {
                FromState = "Initial",
                ToState = "Queued",
                TransitionedAtUtc = job.CreatedAt,
                DurationInStateSeconds = job.ActualStartTime.HasValue
                    ? (int)(job.ActualStartTime.Value - job.CreatedAt).TotalSeconds
                    : null,
                Notes = "Job created and queued"
            });

            // Add started state
            if (job.ActualStartTime.HasValue)
            {
                transitions.Add(new StateTransitionDto
                {
                    FromState = "Queued",
                    ToState = "Printing",
                    TransitionedAtUtc = job.ActualStartTime.Value,
                    DurationInStateSeconds = job.ActualEndTime.HasValue
                        ? (int)(job.ActualEndTime.Value - job.ActualStartTime.Value).TotalSeconds
                        : job.ActualPrintTime.HasValue
                            ? (int)job.ActualPrintTime.Value.TotalSeconds
                            : null,
                    Notes = job.Status == PrintJobStatus.Failed ? $"Failed: {job.FailureReason}" : "Print started"
                });
            }

            // Add completion state
            if (job.ActualEndTime.HasValue)
            {
                transitions.Add(new StateTransitionDto
                {
                    FromState = "Printing",
                    ToState = job.Status.ToString(),
                    TransitionedAtUtc = job.ActualEndTime.Value,
                    DurationInStateSeconds = 0,
                    Notes = $"Job {job.Status.ToString().ToLower()}"
                });
            }

            var totalDuration = job.ActualPrintTime.HasValue ? (int)job.ActualPrintTime.Value.TotalSeconds : (job.ActualEndTime.HasValue
                ? (int)(job.ActualEndTime.Value - (job.ActualStartTime ?? job.CreatedAt)).TotalSeconds
                : (int?)null);

            var estimatedDuration = job.EstimatedPrintTime.HasValue ? (int?)job.EstimatedPrintTime.Value.TotalSeconds : null;

            _logger.LogInformation("Retrieved state history for job {JobId} with {Count} transitions",
                jobId, transitions.Count);

            return new JobStateHistoryDto
            {
                JobId = job.Id.ToString(),
                JobName = job.Name,
                Transitions = transitions,
                TotalDurationSeconds = totalDuration,
                EstimatedDurationSeconds = estimatedDuration,
                VariancePercent = CalculateVariancePercent(estimatedDuration, totalDuration)
            };
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting state history for job {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Get duration analytics comparing estimated vs actual durations
    /// </summary>
    public async Task<DurationAnalyticsDto> GetDurationAnalyticsAsync(
        string? printerId = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var query = _dbContext.PrintJobs
                .Include(pj => pj.AssignedPrinter)
                .Where(pj => pj.Status == PrintJobStatus.Completed || pj.Status == PrintJobStatus.Failed)
                .AsQueryable();

            // Apply date filters
            if (dateFrom.HasValue)
            {
                query = query.Where(pj => pj.ActualEndTime >= dateFrom);
            }
            if (dateTo.HasValue)
            {
                query = query.Where(pj => pj.ActualEndTime <= dateTo);
            }

            // Apply printer filter
            if (!string.IsNullOrEmpty(printerId))
            {
                query = query.Where(pj => pj.AssignedPrinterId.ToString() == printerId);
            }

            var jobs = await query.ToListAsync(cancellationToken);

            if (jobs.Count == 0)
            {
                _logger.LogWarning("No completed jobs found for analytics");
                return new DurationAnalyticsDto();
            }

            // Calculate overall stats
            var estimatedTimes = jobs
                .Where(j => j.EstimatedPrintTime.HasValue)
                .Select(j => j.EstimatedPrintTime!.Value.TotalSeconds) // Use null-forgiving operator
                .ToList();

            var actualTimes = jobs
                .Where(j => j.ActualPrintTime.HasValue)
                .Select(j => j.ActualPrintTime!.Value.TotalSeconds) // Use null-forgiving operator
                .ToList();

            var avgEstimated = estimatedTimes.Any() ? estimatedTimes.Average() : 0;
            var avgActual = actualTimes.Any() ? actualTimes.Average() : 0;
            var accuracy = avgEstimated > 0 ? (1 - Math.Abs(avgActual - avgEstimated) / avgEstimated) * 100 : 0;
            var variance = avgEstimated > 0 ? ((avgActual - avgEstimated) / avgEstimated) * 100 : 0;

            // Group by printer for detailed stats
            var byPrinter = new Dictionary<string, DurationStatsDto>();
            foreach (var printerGroup in jobs.GroupBy(j => j.AssignedPrinterId))
            {
                var printerJobs = printerGroup.ToList();
                var printerName = printerJobs.FirstOrDefault()?.AssignedPrinter?.Name ?? "Unknown";
                var printerIdStr = printerGroup.Key?.ToString() ?? "unassigned";

                var printerEstimated = printerJobs
                    .Where(j => j.EstimatedPrintTime.HasValue)
                    .Select(j => j.EstimatedPrintTime!.Value.TotalSeconds) // Use null-forgiving operator
                    .ToList();

                var printerActual = printerJobs
                    .Where(j => j.ActualPrintTime.HasValue)
                    .Select(j => j.ActualPrintTime!.Value.TotalSeconds) // Use null-forgiving operator
                    .ToList();

                var printerAvgEst = printerEstimated.Any() ? printerEstimated.Average() : 0;
                var printerAvgAct = printerActual.Any() ? printerActual.Average() : 0;
                var printerAccuracy = printerAvgEst > 0
                    ? (1 - Math.Abs(printerAvgAct - printerAvgEst) / printerAvgEst) * 100
                    : 0;
                var printerVariance = printerAvgEst > 0
                    ? ((printerAvgAct - printerAvgEst) / printerAvgEst) * 100
                    : 0;

                byPrinter[printerIdStr] = new DurationStatsDto
                {
                    PrinterId = printerIdStr,
                    PrinterName = printerName,
                    TotalJobs = printerJobs.Count,
                    AverageEstimatedSeconds = printerAvgEst,
                    AverageActualSeconds = printerAvgAct,
                    AccuracyPercent = Math.Max(0, Math.Min(100, printerAccuracy)), // Clamp 0-100
                    VariancePercent = printerVariance,
                    MinActualSeconds = printerActual.Any() ? (int)printerActual.Min() : 0,
                    MaxActualSeconds = printerActual.Any() ? (int)printerActual.Max() : 0
                };
            }

            // Find top performers and those needing attention
            var allStats = byPrinter.Values.OrderByDescending(s => s.AccuracyPercent).ToList();
            var topPerformers = allStats.Take(3).ToList();
            var needsAttention = allStats.OrderBy(s => s.AccuracyPercent).Take(3).ToList();

            _logger.LogInformation("Duration analytics: {TotalJobs} jobs, {AvgEst}s est, {AvgAct}s act, {Accuracy}% accuracy",
                jobs.Count, (int)avgEstimated, (int)avgActual, (int)accuracy);

            return new DurationAnalyticsDto
            {
                TotalJobs = jobs.Count,
                AverageEstimatedSeconds = avgEstimated,
                AverageActualSeconds = avgActual,
                OverallAccuracyPercent = Math.Max(0, Math.Min(100, accuracy)), // Clamp 0-100
                OverallVariancePercent = variance,
                ByPrinter = byPrinter,
                TopPerformers = topPerformers,
                NeedsAttention = needsAttention
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting duration analytics");
            throw;
        }
    }

    // ============= HELPER METHODS =============

    /// <summary>
    /// Calculate variance percentage between estimated and actual duration
    /// </summary>
    private static decimal? CalculateVariancePercent(int? estimated, int? actual)
    {
        if (!estimated.HasValue || !actual.HasValue || estimated.Value == 0)
        {
            return null;
        }

        return ((decimal)(actual.Value - estimated.Value) / estimated.Value) * 100;
    }

    // ============= NOTIFICATION HELPERS (Phase 4.3) =============

    /// <summary>
    /// Send job completion notification to user
    /// NOTE: This method is reserved for future use when job completion events are refactored
    /// to trigger through PrintQueueService instead of through background printer services.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members")]
    private async Task SendJobCompletionNotificationAsync(
        PrintJob job,
        CancellationToken cancellationToken = default)
    {
        if (_notificationService == null)
        {
            _logger.LogWarning("INotificationService not configured - skipping job completion notification for job {JobId}", job.Id);
            return;
        }

        try
        {
            await _notificationService.SendJobCompletedAsync(
                job.Id.ToString(),
                job.Name,
                job.AssignedPrinter?.Name,
                cancellationToken);

            _logger.LogInformation("Job completion notification sent for job {JobId}: {JobName}", job.Id, job.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending job completion notification for job {JobId}", job.Id);
            // Don't rethrow - notification failure shouldn't block queue operations
        }
    }

    /// <summary>
    /// Send job failure notification to user
    /// </summary>
    private async Task SendJobFailureNotificationAsync(
        PrintJob job,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (_notificationService == null)
        {
            _logger.LogWarning("INotificationService not configured - skipping job failure notification for job {JobId}", job.Id);
            return;
        }

        try
        {
            await _notificationService.SendJobFailedAsync(
                job.Id.ToString(),
                job.Name,
                errorMessage ?? "Job failed during printing",
                cancellationToken);

            _logger.LogInformation("Job failure notification sent for job {JobId}: {JobName}", job.Id, job.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending job failure notification for job {JobId}", job.Id);
            // Don't rethrow - notification failure shouldn't block queue operations
        }
    }

    /// <summary>
    /// Send job pause notification to user
    /// </summary>
    private async Task SendJobPauseNotificationAsync(
        PrintJob job,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (_notificationService == null)
        {
            _logger.LogWarning("INotificationService not configured - skipping job pause notification for job {JobId}", job.Id);
            return;
        }

        try
        {
            await _notificationService.SendJobPausedAsync(
                job.Id.ToString(),
                job.Name,
                reason,
                cancellationToken);

            _logger.LogInformation("Job pause notification sent for job {JobId}: {JobName}", job.Id, job.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending job pause notification for job {JobId}", job.Id);
            // Don't rethrow - notification failure shouldn't block queue operations
        }
    }

    /// <summary>
    /// Send job resume notification to user
    /// </summary>
    private async Task SendJobResumeNotificationAsync(
        PrintJob job,
        CancellationToken cancellationToken = default)
    {
        if (_notificationService == null)
        {
            _logger.LogWarning("INotificationService not configured - skipping job resume notification for job {JobId}", job.Id);
            return;
        }

        try
        {
            await _notificationService.SendJobResumedAsync(
                job.Id.ToString(),
                job.Name,
                cancellationToken);

            _logger.LogInformation("Job resume notification sent for job {JobId}: {JobName}", job.Id, job.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending job resume notification for job {JobId}", job.Id);
            // Don't rethrow - notification failure shouldn't block queue operations
        }
    }
}

