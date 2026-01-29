using Farm.Api.Services.Interfaces;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Web.Api.DTOs.PrintQueue;

namespace Farm.Api.Services.PrintQueue;

/// <summary>
/// Service for managing print jobs including CRUD operations, queue management,
/// analytics, timeline visualization, and history tracking.
/// </summary>
public class PrintJobManagementService(
    IPrintJobManagementRepository repository,
    ILogger<PrintJobManagementService> logger,
    IPrintersService printersService,
    IStoragePathService storagePathService,
    INotificationService? notificationService = null,
    IRetryService? retryService = null) : IPrintJobManagementService
{
    private readonly IPrintJobManagementRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ILogger<PrintJobManagementService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IPrintersService _printersService = printersService ?? throw new ArgumentNullException(nameof(printersService));
    private readonly IStoragePathService _storagePathService = storagePathService ?? throw new ArgumentNullException(nameof(storagePathService));
    private readonly INotificationService? _notificationService = notificationService;
    private readonly IRetryService? _retryService = retryService;

    // ============= QUERY OPERATIONS =============

    /// <summary>
    /// Get all queued and printing jobs with file metadata
    /// </summary>
    /// <param name="filterStatus">Optional filter by job status.</param>
    /// <param name="filterModel">Optional filter by printer model name.</param>
    /// <param name="filterMaterial">Optional filter by required material type.</param>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="offset">Number of jobs to skip for pagination.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<List<QueuedPrintJobWithFileMetaDto>> GetAllQueuedJobsAsync(
        string? filterStatus = null,
        string? filterModel = null,
        string? filterMaterial = null,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PrintJobStatus? status = null;
            if (!string.IsNullOrEmpty(filterStatus) &&
                Enum.TryParse<PrintJobStatus>(filterStatus, ignoreCase: true, out PrintJobStatus parsedStatus))
            {
                status = parsedStatus;
            }

            List<PrintJob> jobs = await _repository.GetFilteredJobsAsync(
                status, filterModel, filterMaterial, limit, offset, cancellationToken);

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
    /// <param name="printerId">The unique identifier of the printer.</param>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<List<QueuedPrintJobDto>> GetPrinterQueueAsync(
        string printerId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var printerIdGuid = Guid.Parse(printerId);
            List<PrintJob> jobs = await _repository.GetJobsByPrinterAsync(printerIdGuid, limit, cancellationToken);
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
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueueStatsDto> GetQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            (int queued, int printing, int paused, int completed, int failed) = await _repository.GetQueueStatsAsync(cancellationToken);

            return new QueueStatsDto
            {
                TotalQueued = queued,
                TotalPrinting = printing,
                TotalPaused = paused,
                AverageWaitTimeMinutes = 0 // TODO: Calculate from queue entries
            };
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
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<List<QueuePrinterModelStatsDto>> GetModelStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            List<PrinterModelQueueStats> stats = await _repository.GetModelStatsAsync(cancellationToken);

            return stats.Select(s => new QueuePrinterModelStatsDto
            {
                ModelName = s.ModelName,
                TotalQueued = s.TotalQueued,
                CurrentlyPrinting = s.CurrentlyPrinting,
                OldestQueuedAtUtc = s.OldestQueuedAtUtc,
                AverageQueueWaitMinutes = 0 // TODO: Calculate from historical data
            }).ToList();
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
    /// <param name="limit">Maximum number of history entries to return.</param>
    /// <param name="offset">Number of entries to skip for pagination.</param>
    /// <param name="sortBy">Field to sort results by.</param>
    /// <param name="statuses">Optional list of statuses to filter by (completed, failed, cancelled).</param>
    /// <param name="dateStart">Optional start date filter (inclusive).</param>
    /// <param name="dateEnd">Optional end date filter (inclusive).</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueueHistoryPageDto> GetQueueHistoryAsync(
        int limit = 50,
        int offset = 0,
        string sortBy = "completedAt",
        List<string>? statuses = null,
        DateTime? dateStart = null,
        DateTime? dateEnd = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            (List<PrintJob> jobs, int totalCount, int completedCount, int failedCount, int cancelledCount, long totalPrintTimeSeconds) =
                await _repository.GetHistoryAsync(limit, offset, sortBy, statuses, dateStart, dateEnd, cancellationToken);

            var entries = jobs
                .Select(pj => new QueueHistoryEntryDto
                {
                    Id = pj.Id.ToString(),
                    JobName = pj.Name,
                    PrinterName = pj.AssignedPrinter?.Name ?? "Unassigned",
                    Status = pj.Status.ToString(),
                    CompletionPercentage = pj.Status == PrintJobStatus.Completed ? 100 : 0,
                    StartedAtUtc = pj.ActualStartTime ?? pj.CreatedAt,
                    CompletedAtUtc = pj.ActualEndTime,
                    ActualPrintTimeSeconds = (int?)pj.ActualPrintTime?.TotalSeconds ?? 0,
                    FailureReason = pj.FailureReason
                })
                .ToList();

            // Calculate statistics for the full filtered result set
            int total = completedCount + failedCount + cancelledCount;
            int successRate = total > 0 ? (int)Math.Round((double)completedCount / total * 100) : 0;
            int avgDurationMinutes = total > 0 ? (int)(totalPrintTimeSeconds / 60 / total) : 0;

            return new QueueHistoryPageDto
            {
                Entries = entries,
                TotalCount = totalCount,
                CurrentPage = offset / limit,
                PageSize = limit,
                Stats = new QueueHistoryStatsDto
                {
                    TotalCompleted = completedCount,
                    TotalFailed = failedCount,
                    TotalCancelled = cancelledCount,
                    SuccessRate = successRate,
                    AverageDurationMinutes = avgDurationMinutes,
                    TotalPrintTimeMinutes = totalPrintTimeSeconds / 60
                }
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
    /// <param name="request">The request containing job details to enqueue.</param>
    /// <param name="userId">The unique identifier of the user enqueuing the job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueuedPrintJobDto> EnqueueJobAsync(
        EnqueueQueueJobRequest request,
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(request.GcodeFileId))
            {
                throw new ArgumentException("GcodeFileId is required");
            }

            // Verify gcode file exists
            GcodeFile? gcodeFile = await _repository.GetGcodeFileAsync(Guid.Parse(request.GcodeFileId), cancellationToken);
            if (gcodeFile == null)
            {
                throw new InvalidOperationException($"G-code file {request.GcodeFileId} not found");
            }

            // Create new print job
            // Status is Assigned if a printer is specified, otherwise Queued
            Guid? assignedPrinterId = string.IsNullOrEmpty(request.AssignedPrinterId) ? null : Guid.Parse(request.AssignedPrinterId);
            var job = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = gcodeFile.FileName,
                GcodeFileId = Guid.Parse(request.GcodeFileId),
                AssignedPrinterId = assignedPrinterId,
                Status = assignedPrinterId.HasValue ? PrintJobStatus.Assigned : PrintJobStatus.Queued,
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
            int maxPosition = await _repository.GetMaxQueuePositionAsync(cancellationToken);
            job.QueuePosition = maxPosition + 1;

            _ = await _repository.AddAsync(job, cancellationToken);

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
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="request">The request containing update details.</param>
    /// <param name="userId">The unique identifier of the user performing the update.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueuedPrintJobDto> UpdateJobAsync(
        string jobId,
        UpdateQueueJobRequest request,
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
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
                if (Enum.TryParse<PrintJobStatus>(request.Status, ignoreCase: true, out PrintJobStatus newStatus))
                {
                    job.Status = newStatus;
                }
            }

            if (!string.IsNullOrEmpty(request.FailureReason))
            {
                job.FailureReason = request.FailureReason;
            }

            job.UpdatedAt = DateTime.UtcNow;

            _ = await _repository.UpdateAsync(job, cancellationToken);
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
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="newPriority">The new priority value for the job.</param>
    /// <param name="userId">The unique identifier of the user performing the update.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueuedPrintJobDto> UpdateJobPriorityAsync(
        string jobId,
        int newPriority,
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
            if (job == null)
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            job.Priority = newPriority;
            job.UpdatedAt = DateTime.UtcNow;

            _ = await _repository.UpdateAsync(job, cancellationToken);
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
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="userId">The unique identifier of the user pausing the job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueuedPrintJobDto> PauseJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
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

            _ = await _repository.UpdateAsync(job, cancellationToken);
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
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="userId">The unique identifier of the user resuming the job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueuedPrintJobDto> ResumeJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
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

            _ = await _repository.UpdateAsync(job, cancellationToken);
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
    /// Dispatch a queued/assigned job to its printer to start printing.
    /// This sends the job's G-code file to the assigned printer and starts the print.
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="userId">The unique identifier of the user dispatching the job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>Updated job with Starting/Printing status.</returns>
    public async Task<QueuedPrintJobDto> DispatchJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Load job with related entities
            PrintJob? job = await _repository.GetByIdWithRelationsAsync(Guid.Parse(jobId), cancellationToken);

            if (job == null)
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            // Validate job is in a dispatchable state
            if (job.Status != PrintJobStatus.Queued && job.Status != PrintJobStatus.Assigned)
            {
                throw new InvalidOperationException($"Only Queued or Assigned jobs can be dispatched. Current status: {job.Status}");
            }

            // Validate printer is assigned
            if (job.AssignedPrinterId == null || job.AssignedPrinter == null)
            {
                throw new InvalidOperationException("Cannot dispatch job without an assigned printer. Please assign a printer first.");
            }

            // Validate G-code file exists
            if (job.GcodeFile == null)
            {
                throw new InvalidOperationException($"G-code file not found for job {jobId}");
            }

            // Update status to Starting
            job.Status = PrintJobStatus.Starting;
            job.ActualStartTime = DateTime.UtcNow;
            job.UpdatedAt = DateTime.UtcNow;
            await _repository.SaveChangesAsync(cancellationToken);

            // Use original filename for the printer (not the GUID-based storage filename)
            string printerFileName = job.GcodeFile.Name;

            // Resolve the full local file path: StorageRoot + VirtualPath + FileName
            string gcodeStorageRoot = _storagePathService.GetGcodeStorageDirectory();
            string localFilePath = Path.Combine(gcodeStorageRoot, job.GcodeFile.FilePath.TrimStart('/'), job.GcodeFile.FileName);

            _logger.LogInformation(
                "Dispatching print job {JobId} to printer {PrinterId}: uploading {OriginalName} from {LocalPath}",
                jobId, job.AssignedPrinterId, printerFileName, localFilePath);

            try
            {
                // Validate the local file exists
                if (!System.IO.File.Exists(localFilePath))
                {
                    job.Status = PrintJobStatus.Assigned;
                    job.FailureReason = $"G-code file not found on disk: {localFilePath}";
                    _logger.LogError("G-code file not found on disk for job {JobId}: {LocalPath}", jobId, localFilePath);
                }
                else
                {
                    // Step 1: Upload the file to the printer
                    await using FileStream fileStream = System.IO.File.OpenRead(localFilePath);
                    bool uploadSuccess = await _printersService.UploadGcodeAsync(
                        job.AssignedPrinterId.Value,
                        printerFileName,
                        fileStream,
                        cancellationToken);

                    if (!uploadSuccess)
                    {
                        job.Status = PrintJobStatus.Assigned;
                        job.FailureReason = "Failed to upload G-code file to printer";
                        _logger.LogWarning("Failed to upload G-code to printer for job {JobId}", jobId);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Successfully uploaded {FileName} to printer {PrinterId}",
                            printerFileName, job.AssignedPrinterId);

                        // Step 2: Start the print on the printer using the uploaded filename
                        bool startSuccess = await _printersService.StartPrintFromFileAsync(
                            job.AssignedPrinterId.Value,
                            printerFileName,
                            cancellationToken);

                        if (startSuccess)
                        {
                            job.Status = PrintJobStatus.Printing;
                            _logger.LogInformation("Print job {JobId} successfully started on printer {PrinterId}", jobId, job.AssignedPrinterId);
                        }
                        else
                        {
                            // Revert to Assigned status if start failed
                            job.Status = PrintJobStatus.Assigned;
                            job.FailureReason = "Failed to start print on printer after upload";
                            _logger.LogWarning("Failed to start print job {JobId} on printer {PrinterId} after successful upload", jobId, job.AssignedPrinterId);
                        }
                    }
                }
            }
            catch (Exception printEx)
            {
                // Revert to Assigned status on exception
                job.Status = PrintJobStatus.Assigned;
                job.FailureReason = $"Error starting print: {printEx.Message}";
                _logger.LogError(printEx, "Error dispatching print job {JobId} to printer {PrinterId}", jobId, job.AssignedPrinterId);
            }

            job.UpdatedAt = DateTime.UtcNow;
            await _repository.SaveChangesAsync(cancellationToken);

            // Send notification for job start
            if (job.Status == PrintJobStatus.Printing)
            {
                await SendJobStartNotificationAsync(job, cancellationToken);
            }

            return MapToQueuedPrintJobDto(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching print job {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Cancel a job (remove from queue or stop printing).
    /// If the job is currently Printing or Paused, sends a cancel command to the printer.
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="userId">The unique identifier of the user cancelling the job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task CancelJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
            if (job == null)
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            if (job.Status == PrintJobStatus.Completed || job.Status == PrintJobStatus.Cancelled)
            {
                throw new InvalidOperationException($"Cannot cancel a {job.Status} job");
            }

            // If the job is currently printing or paused on a printer, send cancel command to the printer
            if ((job.Status == PrintJobStatus.Printing || job.Status == PrintJobStatus.Paused || job.Status == PrintJobStatus.Starting)
                && job.AssignedPrinterId.HasValue)
            {
                _logger.LogInformation(
                    "Job {JobId} is {Status} on printer {PrinterId}, sending cancel command to printer",
                    jobId, job.Status, job.AssignedPrinterId.Value);

                bool cancelSuccess = await _printersService.CancelPrintAsync(job.AssignedPrinterId.Value, cancellationToken);

                if (cancelSuccess)
                {
                    _logger.LogInformation(
                        "Successfully sent cancel command to printer {PrinterId} for job {JobId}",
                        job.AssignedPrinterId.Value, jobId);
                }
                else
                {
                    _logger.LogWarning(
                        "Failed to send cancel command to printer {PrinterId} for job {JobId}. " +
                        "Job will be marked as cancelled in database but printer may still be printing.",
                        job.AssignedPrinterId.Value, jobId);
                }
            }

            job.Status = PrintJobStatus.Cancelled;
            job.UpdatedAt = DateTime.UtcNow;
            job.ActualEndTime = DateTime.UtcNow;

            await _repository.SaveChangesAsync(cancellationToken);
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
    /// <param name="jobIds">The list of job identifiers to cancel.</param>
    /// <param name="userId">The unique identifier of the user performing the bulk cancel.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueueBulkOperationResultDto> BulkCancelJobsAsync(
        List<string> jobIds,
        string userId,
        CancellationToken cancellationToken = default)
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
            foreach (string jobId in jobIds)
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

            _logger.LogInformation(
                "Bulk cancel completed: {SuccessCount} succeeded, {FailureCount} failed",
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
    /// <param name="moves">The list of job reorder moves to apply.</param>
    /// <param name="userId">The unique identifier of the user performing the reorder.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueueBulkOperationResultDto> BulkReorderJobsAsync(
        List<QueueJobReorderMove> moves,
        string userId,
        CancellationToken cancellationToken = default)
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
            foreach (QueueJobReorderMove move in moves)
            {
                try
                {
                    PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(move.JobId), cancellationToken);
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
                await _repository.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation(
                "Bulk reorder completed: {SuccessCount} succeeded, {FailureCount} failed",
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
    /// <param name="jobId">The unique identifier of the print job to rerun.</param>
    /// <param name="userId">The unique identifier of the user requesting the rerun.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueuedPrintJobDto> RerunJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(jobId))
            {
                throw new ArgumentException("Job ID is required");
            }

            // Find the job to rerun
            PrintJob originalJob = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken)
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
            int maxPosition = await _repository.GetMaxQueuePositionAsync(cancellationToken);
            newJob.QueuePosition = maxPosition + 1;

            await _repository.AddAsync(newJob, cancellationToken);

            _logger.LogInformation(
                "Job {JobId} rerun as {NewJobId} by user {UserId}",
                originalJob.Id, newJob.Id, userId);

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
    /// Seed print job history from printer history APIs.
    /// Fetches all available history (up to 10,000 jobs per printer) since the
    /// ISupportsHistory interface doesn't support date filtering.
    /// Jobs are identified by (ExternalJobId, SourcePrinterId) composite key.
    /// Existing jobs are updated, new jobs are inserted (AddOrUpdate semantics).
    /// </summary>
    /// <param name="printerIds">Optional list of printer identifiers to seed from. If null, seeds from all printers.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task SeedHistoryFromPrintersAsync(
        List<string>? printerIds = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[HistorySeed] Starting history seeding (fetching all available history)");

        int totalAdded = 0;
        int totalUpdated = 0;
        int totalSkipped = 0;
        int printersProcessed = 0;

        try
        {
            // Get all printers or filter by provided IDs
            List<Printer> printers = await _repository.GetEnabledPrintersAsync(cancellationToken);

            if (printerIds?.Count > 0)
            {
                HashSet<Guid> filterIds = printerIds
                    .Where(id => Guid.TryParse(id, out _))
                    .Select(id => Guid.Parse(id))
                    .ToHashSet();
                printers = printers.Where(p => filterIds.Contains(p.Id)).ToList();
            }

            _logger.LogInformation("[HistorySeed] Processing {PrinterCount} printer(s)", printers.Count);

            foreach (Printer printer in printers)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    (int added, int updated, int skipped) = await SeedHistoryFromSinglePrinterAsync(
                        printer, cancellationToken);

                    totalAdded += added;
                    totalUpdated += updated;
                    totalSkipped += skipped;
                    printersProcessed++;

                    _logger.LogInformation(
                        "[HistorySeed] Printer {PrinterName} ({PrinterId}): Added={Added}, Updated={Updated}, Skipped={Skipped}",
                        printer.Name, printer.Id, added, updated, skipped);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[HistorySeed] Failed to seed history from printer {PrinterName} ({PrinterId})",
                        printer.Name, printer.Id);
                }
            }

            _logger.LogInformation(
                "[HistorySeed] Completed: Printers={PrintersProcessed}, Added={TotalAdded}, Updated={TotalUpdated}, Skipped={TotalSkipped}",
                printersProcessed, totalAdded, totalUpdated, totalSkipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HistorySeed] Error seeding queue history");
            throw;
        }
    }

    /// <summary>
    /// Seeds history from a single printer using AddOrUpdate semantics.
    /// Uses incremental seeding: first run fetches all history, subsequent runs
    /// only fetch jobs newer than LastHistorySeedUtc (server-side for Moonraker,
    /// client-side filtering for OctoPrint). This avoids re-fetching and
    /// re-processing the entire history on every run.
    /// </summary>
    private async Task<(int Added, int Updated, int Skipped)> SeedHistoryFromSinglePrinterAsync(
        Printer printer,
        CancellationToken cancellationToken)
    {
        int added = 0;
        int updated = 0;
        int skipped = 0;

        bool isInitialSeed = !printer.LastHistorySeedUtc.HasValue;
        DateTime? seedSinceUtc = printer.LastHistorySeedUtc;
        DateTime latestJobTimestamp = printer.LastHistorySeedUtc ?? DateTime.MinValue;

        // Get history from printer via PrintersService
        // Pass 'since' for incremental seeding - Moonraker will filter server-side,
        // OctoPrint will return all and we filter client-side below.
        HistoryListResponse history = await _printersService.GetHistoryListAsync(
            printer.Id,
            limit: isInitialSeed ? 10000 : 1000, // Full fetch on initial, smaller on incremental
            start: 0,
            since: seedSinceUtc, // Pass last seed time for incremental fetching
            before: null,
            order: null,
            cancellationToken);

        if (history.Jobs.Length == 0)
        {
            _logger.LogDebug("[HistorySeed] No history jobs from printer {PrinterName}", printer.Name);
            return (0, 0, 0);
        }

        _logger.LogDebug(
            "[HistorySeed] Retrieved {JobCount} history jobs from printer {PrinterName} (initial={IsInitial})",
            history.Jobs.Length, printer.Name, isInitialSeed);

        // Get all existing seeded jobs for this printer to check for duplicates
        HashSet<string> existingExternalJobIds = await _repository.GetExternalJobIdsForPrinterAsync(
            printer.Id, cancellationToken);

        foreach (HistoryJob historyJob in history.Jobs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Skip jobs without a valid external ID
            if (string.IsNullOrWhiteSpace(historyJob.JobId))
            {
                skipped++;
                continue;
            }

            // Convert job timestamp for incremental filtering
            DateTime jobTimestampUtc = DateTimeOffset.FromUnixTimeSeconds((long)historyJob.StartTime).UtcDateTime;

            // On incremental seed, skip jobs older than or equal to last seed timestamp.
            // This client-side filtering is needed for OctoPrint (which doesn't support server-side filtering).
            // Moonraker already filters server-side via the 'since' parameter, but this acts as a safety net.
            if (!isInitialSeed && seedSinceUtc.HasValue && jobTimestampUtc <= seedSinceUtc.Value)
            {
                skipped++;
                continue;
            }

            // Track the latest job timestamp for updating LastHistorySeedUtc
            if (jobTimestampUtc > latestJobTimestamp)
            {
                latestJobTimestamp = jobTimestampUtc;
            }

            try
            {
                if (existingExternalJobIds.Contains(historyJob.JobId))
                {
                    // Job exists - update it (only on initial seed or if somehow we see it again)
                    if (isInitialSeed)
                    {
                        PrintJob? existingJob = await _repository.GetByExternalIdAsync(
                            printer.Id, historyJob.JobId, cancellationToken);

                        if (existingJob != null)
                        {
                            UpdatePrintJobFromHistory(existingJob, historyJob);
                            existingJob.UpdatedAt = DateTime.UtcNow;
                            updated++;
                        }
                    }
                    else
                    {
                        // On incremental, skip already-known jobs
                        skipped++;
                    }
                }
                else
                {
                    // New job - create it
                    PrintJob newJob = await CreatePrintJobFromHistoryAsync(historyJob, printer.Id, cancellationToken);

                    // Using sync Add() is intentional - we're batching multiple entities and calling SaveChangesAsync at the end.
                    // AddAsync() is only needed when the entity has value-generated properties requiring DB interaction.
#pragma warning disable CA1849 // Call async methods when in an async method
                    _repository.Add(newJob);
#pragma warning restore CA1849
                    existingExternalJobIds.Add(historyJob.JobId); // Track for this batch
                    added++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HistorySeed] Failed to process history job {JobId}", historyJob.JobId);
                skipped++;
            }
        }

        // Save all changes for this printer
        if (added > 0 || updated > 0)
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }

        // Update the printer's last seed timestamp if we processed any jobs
        if (latestJobTimestamp > (printer.LastHistorySeedUtc ?? DateTime.MinValue))
        {
            printer.LastHistorySeedUtc = latestJobTimestamp;
            await _repository.UpdatePrinterLastHistorySeedAsync(printer.Id, latestJobTimestamp, cancellationToken);
            _logger.LogDebug(
                "[HistorySeed] Updated LastHistorySeedUtc for printer {PrinterName} to {Timestamp}",
                printer.Name, latestJobTimestamp);
        }

        return (added, updated, skipped);
    }

    /// <summary>
    /// Creates a new PrintJob entity from a HistoryJob record.
    /// </summary>
    private async Task<PrintJob> CreatePrintJobFromHistoryAsync(
        HistoryJob historyJob,
        Guid printerId,
        CancellationToken cancellationToken = default)
    {
        DateTime startTime = DateTimeOffset.FromUnixTimeSeconds((long)historyJob.StartTime).UtcDateTime;
        DateTime? endTime = historyJob.EndTime.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds((long)historyJob.EndTime.Value).UtcDateTime
            : null;

        // Extract nozzle diameter and material type from metadata
        decimal? nozzleDiameter = ExtractNozzleDiameterFromMetadata(historyJob.Metadata);
        string? materialType = ExtractMaterialTypeFromMetadata(historyJob.Metadata);
        TimeSpan? estimatedPrintTime = ExtractEstimatedPrintTimeFromMetadata(historyJob.Metadata);
        double? estimatedFilamentUsage = ExtractEstimatedFilamentUsageFromMetadata(historyJob.Metadata);

        return new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = Path.GetFileNameWithoutExtension(historyJob.Filename) ?? "Unknown",
            Status = MapHistoryStatusToPrintJobStatus(historyJob.Status),
            Priority = 0,
            QueuePosition = 0,
            ActualStartTime = startTime,
            ActualEndTime = endTime,
            ActualPrintTime = endTime.HasValue ? endTime.Value - startTime : null,
            ActualFilamentUsage = historyJob.FilamentUsed > 0 ? historyJob.FilamentUsed * 0.003 : null, // mm to grams: ~3g per meter for 1.75mm filament
            CreatedAt = startTime,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = startTime,

            // Nozzle and material from metadata
            RequiredNozzleDiameter = nozzleDiameter,
            RequiredMaterialType = materialType,
            EstimatedPrintTime = estimatedPrintTime,
            EstimatedFilamentUsage = estimatedFilamentUsage,

            // History seeding tracking
            ExternalJobId = historyJob.JobId,
            SourcePrinterId = printerId,
            WasSeededFromHistory = true,

            // Associate with printer
            AssignedPrinterId = printerId,

            // Try to find matching G-code file by filename
            GcodeFileId = await FindGcodeFileIdByFilenameAsync(historyJob.Filename, cancellationToken)
        };
    }

    /// <summary>
    /// Updates an existing PrintJob entity with data from a HistoryJob record.
    /// </summary>
    private void UpdatePrintJobFromHistory(PrintJob existingJob, HistoryJob historyJob)
    {
        DateTime startTime = DateTimeOffset.FromUnixTimeSeconds((long)historyJob.StartTime).UtcDateTime;
        DateTime? endTime = historyJob.EndTime.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds((long)historyJob.EndTime.Value).UtcDateTime
            : null;

        // Update mutable fields
        existingJob.Status = MapHistoryStatusToPrintJobStatus(historyJob.Status);
        existingJob.ActualStartTime = startTime;
        existingJob.ActualEndTime = endTime;
        existingJob.ActualPrintTime = endTime.HasValue ? endTime.Value - startTime : null;
        existingJob.ActualFilamentUsage = historyJob.FilamentUsed > 0 ? historyJob.FilamentUsed * 0.003 : null; // mm to grams: ~3g per meter for 1.75mm filament

        // Update nozzle and material from metadata if not already set
        if (!existingJob.RequiredNozzleDiameter.HasValue)
        {
            existingJob.RequiredNozzleDiameter = ExtractNozzleDiameterFromMetadata(historyJob.Metadata);
        }

        if (string.IsNullOrEmpty(existingJob.RequiredMaterialType))
        {
            existingJob.RequiredMaterialType = ExtractMaterialTypeFromMetadata(historyJob.Metadata);
        }

        if (!existingJob.EstimatedPrintTime.HasValue)
        {
            existingJob.EstimatedPrintTime = ExtractEstimatedPrintTimeFromMetadata(historyJob.Metadata);
        }

        if (!existingJob.EstimatedFilamentUsage.HasValue)
        {
            existingJob.EstimatedFilamentUsage = ExtractEstimatedFilamentUsageFromMetadata(historyJob.Metadata);
        }

        // Don't overwrite printer assignment or G-code file association
    }

    /// <summary>
    /// Maps Moonraker history status strings to PrintJobStatus enum.
    /// </summary>
    private static PrintJobStatus MapHistoryStatusToPrintJobStatus(string historyStatus)
    {
        return historyStatus?.ToLowerInvariant() switch
        {
            "completed" => PrintJobStatus.Completed,
            "cancelled" => PrintJobStatus.Cancelled,
            "error" => PrintJobStatus.Failed,
            "in_progress" or "printing" => PrintJobStatus.Printing,
            "paused" => PrintJobStatus.Paused,
            "standby" or "ready" => PrintJobStatus.Queued,
            _ => PrintJobStatus.Completed // Default for unknown statuses from history
        };
    }

    /// <summary>
    /// Attempts to find a matching G-code file by filename.
    /// Returns null if no match found (GcodeFileId is nullable for history-seeded jobs).
    /// </summary>
    private async Task<Guid?> FindGcodeFileIdByFilenameAsync(string filename, CancellationToken cancellationToken = default)
    {
        // Try to find by original name (without path)
        string name = Path.GetFileName(filename);

        GcodeFile? match = await _repository.FindGcodeFileByFilenameAsync(name, cancellationToken);

        return match?.Id;
    }

    /// <summary>
    /// Extracts nozzle diameter from Moonraker history metadata.
    /// Moonraker returns metadata from gcode file, keys match slicer output.
    /// </summary>
    private static decimal? ExtractNozzleDiameterFromMetadata(Dictionary<string, object>? metadata)
    {
        if (metadata == null)
        {
            return null;
        }

        // Moonraker uses "nozzle_diameter" key from gcode metadata
        // Can be a single value or array (for multi-extruder setups)
        string[] keys = ["nozzle_diameter", "NozzleDiameter", "nozzleDiameter"];

        foreach (string key in keys)
        {
            if (metadata.TryGetValue(key, out object? value))
            {
                return value switch
                {
                    decimal d => d,
                    double d => (decimal)d,
                    float f => (decimal)f,
                    int i => i,
                    long l => l,
                    string s when decimal.TryParse(s, out decimal result) => result,
                    System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array =>
                        jsonElement.GetArrayLength() > 0 && jsonElement[0].TryGetDecimal(out decimal first) ? first : null,
                    System.Text.Json.JsonElement jsonElement when jsonElement.TryGetDecimal(out decimal d) => d,
                    _ => null
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts material/filament type from Moonraker history metadata.
    /// </summary>
    private static string? ExtractMaterialTypeFromMetadata(Dictionary<string, object>? metadata)
    {
        if (metadata == null)
        {
            return null;
        }

        // Moonraker uses various keys for material type
        string[] keys = ["filament_type", "filament_name", "material", "MATERIAL", "Material"];

        foreach (string key in keys)
        {
            if (metadata.TryGetValue(key, out object? value))
            {
                return value switch
                {
                    string s when !string.IsNullOrWhiteSpace(s) => s.Trim(),
                    System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array =>
                        jsonElement.GetArrayLength() > 0 ? jsonElement[0].GetString()?.Trim() : null,
                    System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.String =>
                        jsonElement.GetString()?.Trim(),
                    _ => null
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts estimated print time from Moonraker history metadata.
    /// </summary>
    private static TimeSpan? ExtractEstimatedPrintTimeFromMetadata(Dictionary<string, object>? metadata)
    {
        if (metadata == null)
        {
            return null;
        }

        // Moonraker uses "estimated_time" in seconds
        string[] keys = ["estimated_time", "print_time", "EstimatedTime", "printTime"];

        foreach (string key in keys)
        {
            if (metadata.TryGetValue(key, out object? value))
            {
                double? seconds = value switch
                {
                    double d => d,
                    float f => f,
                    int i => i,
                    long l => l,
                    decimal d => (double)d,
                    string s when double.TryParse(s, out double result) => result,
                    System.Text.Json.JsonElement jsonElement when jsonElement.TryGetDouble(out double d) => d,
                    _ => null
                };

                if (seconds.HasValue && seconds.Value > 0)
                {
                    return TimeSpan.FromSeconds(seconds.Value);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts estimated filament usage from Moonraker history metadata (in mm or grams).
    /// </summary>
    private static double? ExtractEstimatedFilamentUsageFromMetadata(Dictionary<string, object>? metadata)
    {
        if (metadata == null)
        {
            return null;
        }

        // Moonraker uses "filament_total" for total filament in mm
        string[] keys = ["filament_total", "filament_used", "FilamentTotal", "filamentTotal"];

        foreach (string key in keys)
        {
            if (metadata.TryGetValue(key, out object? value))
            {
                double? mm = value switch
                {
                    double d => d,
                    float f => f,
                    int i => i,
                    long l => l,
                    decimal d => (double)d,
                    string s when double.TryParse(s, out double result) => result,
                    System.Text.Json.JsonElement jsonElement when jsonElement.TryGetDouble(out double d) => d,
                    _ => null
                };

                if (mm.HasValue && mm.Value > 0)
                {
                    // Convert from mm to grams (approximate: 1m of 1.75mm PLA = ~3g)
                    return mm.Value * 0.003;
                }
            }
        }

        return null;
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
            // Name = original filename for display (prefer GcodeFile.Name, fallback to job.Name for history-seeded jobs)
            Name = job.GcodeFile?.Name ?? job.Name,
            GcodeFileId = job.GcodeFileId?.ToString(),
            // FileName = internal GUID-based path (null for history-seeded jobs without GcodeFile)
            FileName = job.GcodeFile?.FileName,
            AssignedPrinterId = job.AssignedPrinterId?.ToString(),
            PrinterName = job.AssignedPrinter?.Name, // Denormalized printer name for display
            PrinterModel = job.AssignedPrinter?.Model?.Name, // Denormalized printer model for display
            Status = job.Status.ToString(),
            Priority = job.Priority,
            QueuePosition = job.QueuePosition,
            RequiredNozzleDiameter = job.RequiredNozzleDiameter,
            RequiredMaterialType = job.RequiredMaterialType,
            RequiredCapabilities = job.RequiredCapabilities,
            EstimatedPrintTimeSeconds = (int?)job.EstimatedPrintTime?.TotalSeconds,
            EstimatedFilamentUsageGrams = job.EstimatedFilamentUsage,
            ActualStartTimeUtc = job.ActualStartTime,
            ActualEndTimeUtc = job.ActualEndTime,
            ActualPrintTimeSeconds = (int?)job.ActualPrintTime?.TotalSeconds,
            ActualFilamentUsageGrams = job.ActualFilamentUsage,
            FailureReason = job.FailureReason,
            Notes = job.Notes,
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
            Name = file.Name, // Original filename for display
            FileName = file.FileName, // GUID-based filename on disk
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
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueuedPrintJobDto?> GetJobByIdAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return null;
            }

            PrintJob? job = await _repository.GetByIdWithGcodeFileAsync(Guid.Parse(jobId), cancellationToken);

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
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="updates">The update details to apply to the job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueuedPrintJobDto?> UpdateJobDetailsAsync(
        string jobId,
        UpdateJobDetailsRequest updates,
        CancellationToken cancellationToken = default)
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

            PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);

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
            await _repository.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Job {JobId} details updated: Name={Name}, Priority={Priority}, Notes={NotesLength}",
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
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="notes">The notes to set on the job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<bool> UpdateJobNotesAsync(
        string jobId,
        string? notes,
        CancellationToken cancellationToken = default)
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

            PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);

            if (job == null)
            {
                return false;
            }

            job.Notes = notes;
            job.UpdatedAt = DateTime.UtcNow;
            await _repository.SaveChangesAsync(cancellationToken);

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
    /// <param name="dateFrom">Optional start date filter.</param>
    /// <param name="dateTo">Optional end date filter.</param>
    /// <param name="printerId">Optional filter by printer identifier.</param>
    /// <param name="filterStatus">Optional filter by job status.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<IEnumerable<TimelineEventDto>> GetTimelineAsync(
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        string? printerId = null,
        string? filterStatus = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid? printerGuid = !string.IsNullOrEmpty(printerId) ? Guid.Parse(printerId) : null;
            PrintJobStatus? statusFilter = null;

            // Apply status filter
            if (!string.IsNullOrEmpty(filterStatus) &&
                Enum.TryParse<PrintJobStatus>(filterStatus, ignoreCase: true, out PrintJobStatus status))
            {
                statusFilter = status;
            }

            List<PrintJob> jobs = await _repository.GetTimelineJobsAsync(
                dateFrom,
                dateTo,
                printerGuid,
                statusFilter,
                limit,
                cancellationToken);

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
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<JobStateHistoryDto> GetJobStateHistoryAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Job ID is required", nameof(jobId));
            }

            PrintJob? job = await _repository.GetJobWithStateHistoryAsync(Guid.Parse(jobId), cancellationToken);

            if (job == null)
            {
                throw new ArgumentException($"Job {jobId} not found", nameof(jobId));
            }

            // Build state transitions from job history
            List<StateTransitionDto> transitions = [];

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

            int? totalDuration = job.ActualPrintTime.HasValue ? (int)job.ActualPrintTime.Value.TotalSeconds : (job.ActualEndTime.HasValue
                ? (int)(job.ActualEndTime.Value - (job.ActualStartTime ?? job.CreatedAt)).TotalSeconds
                : (int?)null);

            int? estimatedDuration = job.EstimatedPrintTime.HasValue ? (int?)job.EstimatedPrintTime.Value.TotalSeconds : null;

            _logger.LogInformation(
                "Retrieved state history for job {JobId} with {Count} transitions",
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
    /// <param name="printerId">Optional filter by printer identifier.</param>
    /// <param name="dateFrom">Optional start date filter.</param>
    /// <param name="dateTo">Optional end date filter.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<DurationAnalyticsDto> GetDurationAnalyticsAsync(
        string? printerId = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid? printerGuid = !string.IsNullOrEmpty(printerId) ? Guid.Parse(printerId) : null;

            List<PrintJob> jobs = await _repository.GetCompletedJobsForAnalyticsAsync(
                printerGuid, dateFrom, dateTo, cancellationToken);

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

            double avgEstimated = estimatedTimes.Any() ? estimatedTimes.Average() : 0;
            double avgActual = actualTimes.Any() ? actualTimes.Average() : 0;
            double accuracy = avgEstimated > 0 ? (1 - (Math.Abs(avgActual - avgEstimated) / avgEstimated)) * 100 : 0;
            double variance = avgEstimated > 0 ? (avgActual - avgEstimated) / avgEstimated * 100 : 0;

            // Group by printer for detailed stats
            var byPrinter = new Dictionary<string, DurationStatsDto>();
            foreach (IGrouping<Guid?, PrintJob> printerGroup in jobs.GroupBy(j => j.AssignedPrinterId))
            {
                var printerJobs = printerGroup.ToList();
                string printerName = printerJobs.FirstOrDefault()?.AssignedPrinter?.Name ?? "Unknown";
                string printerIdStr = printerGroup.Key?.ToString() ?? "unassigned";

                var printerEstimated = printerJobs
                    .Where(j => j.EstimatedPrintTime.HasValue)
                    .Select(j => j.EstimatedPrintTime!.Value.TotalSeconds) // Use null-forgiving operator
                    .ToList();

                var printerActual = printerJobs
                    .Where(j => j.ActualPrintTime.HasValue)
                    .Select(j => j.ActualPrintTime!.Value.TotalSeconds) // Use null-forgiving operator
                    .ToList();

                double printerAvgEst = printerEstimated.Any() ? printerEstimated.Average() : 0;
                double printerAvgAct = printerActual.Any() ? printerActual.Average() : 0;
                double printerAccuracy = printerAvgEst > 0
                    ? (1 - (Math.Abs(printerAvgAct - printerAvgEst) / printerAvgEst)) * 100
                    : 0;
                double printerVariance = printerAvgEst > 0
                    ? (printerAvgAct - printerAvgEst) / printerAvgEst * 100
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

            _logger.LogInformation(
                "Duration analytics: {TotalJobs} jobs, {AvgEst}s est, {AvgAct}s act, {Accuracy}% accuracy",
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
    /// <param name="estimated">The estimated duration in seconds.</param>
    /// <param name="actual">The actual duration in seconds.</param>
    private static decimal? CalculateVariancePercent(int? estimated, int? actual)
    {
        return !estimated.HasValue || !actual.HasValue || estimated.Value == 0
            ? null
            : (decimal)(actual.Value - estimated.Value) / estimated.Value * 100;
    }

    // ============= NOTIFICATION HELPERS (Phase 4.3) =============

    /// <summary>
    /// Send job completion notification to user
    /// NOTE: This method is reserved for future use when job completion events are refactored
    /// to trigger through PrintQueueService instead of through background printer services.
    /// </summary>
    /// <param name="job">The print job that was completed.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "This method is reserved for future use.")]
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
    /// <param name="job">The print job that failed.</param>
    /// <param name="errorMessage">Optional error message describing the failure.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
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
    /// <param name="job">The print job that was paused.</param>
    /// <param name="reason">Optional reason for pausing the job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
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
    /// <param name="job">The print job that was resumed.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
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

    /// <summary>
    /// Send job start notification to user (when job is dispatched to printer)
    /// </summary>
    /// <param name="job">The print job that was started.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    private async Task SendJobStartNotificationAsync(
        PrintJob job,
        CancellationToken cancellationToken = default)
    {
        if (_notificationService == null)
        {
            _logger.LogWarning("INotificationService not configured - skipping job start notification for job {JobId}", job.Id);
            return;
        }

        try
        {
            await _notificationService.SendJobStartedAsync(
                job.Id.ToString(),
                job.Name,
                job.AssignedPrinter?.Name,
                cancellationToken);

            _logger.LogInformation("Job start notification sent for job {JobId}: {JobName}", job.Id, job.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending job start notification for job {JobId}", job.Id);

            // Don't rethrow - notification failure shouldn't block queue operations
        }
    }

    // ============= RETRY OPERATIONS (Phase 4.4) =============

    /// <summary>
    /// Handle job failure and initiate retry if appropriate
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="failureReason">The reason for the job failure.</param>
    /// <param name="errorCategory">The category of error that caused the failure.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task HandleJobFailureWithRetryAsync(
        Guid jobId,
        string failureReason,
        ErrorCategory errorCategory,
        CancellationToken cancellationToken = default)
    {
        if (_retryService == null)
        {
            _logger.LogWarning("IRetryService not configured - skipping retry handling for job {JobId}", jobId);
            return;
        }

        try
        {
            bool shouldRetry = await _retryService.ShouldRetryAsync(jobId, errorCategory, cancellationToken);

            if (shouldRetry)
            {
                JobRetry jobRetry = await _retryService.CreateRetryAsync(
                    jobId,
                    errorCategory,
                    failureReason,
                    cancellationToken);

                _logger.LogInformation(
                    "Job {JobId} failure handled with retry: Attempt={Attempt}, ScheduledTime={ScheduledTime}",
                    jobId, jobRetry.AttemptNumber, jobRetry.ScheduledRetryTime);
            }
            else
            {
                _logger.LogInformation(
                    "Job {JobId} failure not eligible for retry: {Reason}",
                    jobId, failureReason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling job failure with retry for job {JobId}", jobId);

            // Don't rethrow - retry handling failure shouldn't block queue operations
        }
    }

    /// <summary>
    /// Get retry history for a specific job
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<IEnumerable<JobRetry>> GetJobRetryHistoryAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return _retryService == null ? Enumerable.Empty<JobRetry>() : await _retryService.GetRetryHistoryAsync(jobId, cancellationToken);
    }

    /// <summary>
    /// Get all pending retries that are due to execute
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<IEnumerable<JobRetry>> GetDueRetriesAsync(CancellationToken cancellationToken = default)
    {
        return _retryService == null ? Enumerable.Empty<JobRetry>() : await _retryService.GetDueRetriesAsync(cancellationToken);
    }
}
