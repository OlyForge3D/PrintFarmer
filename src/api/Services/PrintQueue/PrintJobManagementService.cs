using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.DTOs.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    IHubContext<PrinterHub> hubContext,
    IStoredFileOperationsService fileOperations,
    IPrinterStatusCacheReader printerStatusCache,
    INotificationService? notificationService = null,
    IRetryService? retryService = null,
    IPrinterStatusRefreshService? printerStatusRefreshService = null,
    IJobCostCalculationService? jobCostCalculationService = null,
    ICameraSnapshotService? cameraSnapshotService = null,
    IServiceScopeFactory? serviceScopeFactory = null,
    ISettingsService? settingsService = null,
    Farm.Infrastructure.Services.Spoolman.IFilamentCoverageBroadcaster? coverageBroadcaster = null,
    IPartOutputSnapshotService? partOutputSnapshotService = null,
    IDispatchClaimService? dispatchClaimService = null,
    AppDbContext? appDbContext = null,
    IDbOutboxSequenceAllocator? outboxSequenceAllocator = null,
    IQueuePositionAllocator? queuePositionAllocator = null,
    IQueueResourceAuthorizationService? resourceAuthorization = null) : IPrintJobManagementService
{
    private const string DispatchArtifactUnavailable =
        "The G-code artifact is unavailable for dispatch.";

    private const string DispatchPrinterFailure =
        "The printer could not start the dispatched job.";

    private const string DispatchUnexpectedFailure =
        "The job could not be dispatched.";

    private readonly IPrintJobManagementRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ILogger<PrintJobManagementService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IPrintersService _printersService = printersService ?? throw new ArgumentNullException(nameof(printersService));
    private readonly IStoragePathService _storagePathService = storagePathService ?? throw new ArgumentNullException(nameof(storagePathService));
    private readonly IHubContext<PrinterHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly IStoredFileOperationsService _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
    private readonly IPrinterStatusCacheReader _printerStatusCache = printerStatusCache ?? throw new ArgumentNullException(nameof(printerStatusCache));
    private readonly INotificationService? _notificationService = notificationService;
    private readonly IRetryService? _retryService = retryService;
    private readonly IPrinterStatusRefreshService? _printerStatusRefreshService = printerStatusRefreshService;
    private readonly IJobCostCalculationService? _jobCostCalculationService = jobCostCalculationService;
    private readonly ICameraSnapshotService? _cameraSnapshotService = cameraSnapshotService;
    private readonly IServiceScopeFactory? _serviceScopeFactory = serviceScopeFactory;
    private readonly ISettingsService? _settingsService = settingsService;
    private readonly Farm.Infrastructure.Services.Spoolman.IFilamentCoverageBroadcaster? _coverageBroadcaster = coverageBroadcaster;
    private readonly IPartOutputSnapshotService? _partOutputSnapshotService = partOutputSnapshotService;
    private readonly IDispatchClaimService? _dispatchClaimService = dispatchClaimService;
    private readonly AppDbContext? _appDbContext = appDbContext;
    private readonly IDbOutboxSequenceAllocator? _outboxSequenceAllocator = outboxSequenceAllocator;
    private readonly IQueuePositionAllocator? _queuePositionAllocator = queuePositionAllocator;
    private readonly IQueueResourceAuthorizationService? _resourceAuthorization =
        resourceAuthorization;

    private const int QueuePlanningMaxJobs = 5000;
    private const int DefaultEstimatedPrintMinutes = 90;
    private const int MinimumRemainingPrintMinutes = 5;

    private sealed record HistorySyncOptions(
        bool ActiveOnly,
        bool AllowInitialBackfill,
        bool UseSharedHistoryWatermark,
        bool PersistHistoryWatermark,
        bool UpdateKnownJobsOnIncremental,
        string LogPrefix,
        int InitialFetchLimit,
        int IncrementalFetchLimit);

    private static readonly HistorySyncOptions HistorySeedingOptions = new(
        ActiveOnly: false,
        AllowInitialBackfill: true,
        UseSharedHistoryWatermark: true,
        PersistHistoryWatermark: true,
        UpdateKnownJobsOnIncremental: false,
        LogPrefix: "HistorySeed",
        InitialFetchLimit: 10000,
        IncrementalFetchLimit: 1000);

    private static readonly HistorySyncOptions ActiveExternalSyncOptions = new(
        ActiveOnly: true,
        AllowInitialBackfill: false,
        UseSharedHistoryWatermark: false,
        PersistHistoryWatermark: false,
        UpdateKnownJobsOnIncremental: true,
        LogPrefix: "ActiveExternalSync",
        InitialFetchLimit: 1000,
        IncrementalFetchLimit: 1000);

    private static readonly ConcurrentDictionary<Guid, PrinterSyncLockState> PrinterHistorySyncLocks = new();
    private static readonly TimeSpan PrinterHistorySyncLockIdleTtl = TimeSpan.FromMinutes(15);
    private static int _historySyncReleaseCounter;

    private sealed class PrinterSyncLockState
    {
        private int _referenceCount;
        private int _retired;
        private long _lastUsedUtcTicks = DateTime.UtcNow.Ticks;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public static PrinterSyncLockState CreateWithReference()
        {
            var state = new PrinterSyncLockState();
            state._referenceCount = 1;
            return state;
        }

        public bool TryAddReference()
        {
            while (true)
            {
                if (Volatile.Read(ref _retired) == 1)
                {
                    return false;
                }

                int current = Volatile.Read(ref _referenceCount);
                if (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public int ReleaseReferenceAndMarkUsed()
        {
            Volatile.Write(ref _lastUsedUtcTicks, DateTime.UtcNow.Ticks);
            return Interlocked.Decrement(ref _referenceCount);
        }

        public int ReferenceCount => Volatile.Read(ref _referenceCount);

        public bool IsIdleFor(TimeSpan threshold, DateTime utcNow)
        {
            long idleTicks = utcNow.Ticks - Volatile.Read(ref _lastUsedUtcTicks);
            return idleTicks >= threshold.Ticks;
        }

        public bool TryRetireIfUnused()
        {
            if (Interlocked.CompareExchange(ref _retired, 1, 0) != 0)
            {
                return false;
            }

            if (Volatile.Read(ref _referenceCount) == 0)
            {
                return true;
            }

            Volatile.Write(ref _retired, 0);
            return false;
        }
    }

    // ============= QUERY OPERATIONS =============

    /// <summary>
    /// Get all queued and printing jobs with file metadata
    /// </summary>
    /// <param name="filterStatus">Optional filter by job status.</param>
    /// <param name="filterModel">Optional filter by printer model name.</param>
    /// <param name="filterMaterial">Optional filter by required material type.</param>
    /// <param name="deadlineStart">Optional inclusive lower bound for job deadlines.</param>
    /// <param name="deadlineEnd">Optional inclusive upper bound for job deadlines.</param>
    /// <param name="sortBy">Sort mode (priority, deadline, deadline_desc).</param>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="offset">Number of jobs to skip for pagination.</param>
    /// <param name="queuedFrom">Optional inclusive lower bound for when the job was queued.</param>
    /// <param name="queuedTo">Optional inclusive upper bound for when the job was queued.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<List<QueuedPrintJobWithFileMetaDto>> GetAllQueuedJobsAsync(
        string? filterStatus = null,
        string? filterModel = null,
        string? filterMaterial = null,
        DateTime? deadlineStart = null,
        DateTime? deadlineEnd = null,
        string sortBy = "priority",
        int limit = 100,
        int offset = 0,
        DateTime? queuedFrom = null,
        DateTime? queuedTo = null,
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

            // The active queue reflects current state, so it must not be constrained by the
            // queue-date window (that window belongs to terminal/History views). Applying it here
            // hid still-active jobs older than the window while the stats count still included them,
            // causing a count/list mismatch. Only honor queuedFrom/queuedTo for terminal views.
            bool isTerminalView = status.HasValue && IsTerminalStatus(status.Value);
            DateTime? effectiveQueuedFrom = isTerminalView ? queuedFrom : null;
            DateTime? effectiveQueuedTo = isTerminalView ? queuedTo : null;

            List<PrintJob> jobs = await _repository.GetFilteredJobsAsync(
                status, filterModel, filterMaterial, deadlineStart, deadlineEnd, sortBy, limit, offset, effectiveQueuedFrom, effectiveQueuedTo, cancellationToken);

            Dictionary<Guid, string?> dispatchVersions = [];
            Dictionary<Guid, QueueDispatchAttempt> latestAttempts = [];
            if (_appDbContext is not null)
            {
                Guid[] jobIds = jobs.Select(job => job.Id).ToArray();
                Guid[] printerIds = jobs
                    .Where(job => job.AssignedPrinterId.HasValue)
                    .Select(job => job.AssignedPrinterId!.Value)
                    .Distinct()
                    .ToArray();
                dispatchVersions = await _appDbContext.PrinterDispatchStates
                    .AsNoTracking()
                    .Where(state => printerIds.Contains(state.PrinterId))
                    .ToDictionaryAsync(
                        state => state.PrinterId,
                        state => state.RowVersion is { Length: > 0 }
                            ? Convert.ToBase64String(state.RowVersion)
                            : null,
                        cancellationToken);
                List<QueueDispatchAttempt> attempts = await _appDbContext
                    .QueueDispatchAttempts
                    .AsNoTracking()
                    .Where(attempt =>
                        attempt.PrintJobId.HasValue &&
                        jobIds.Contains(attempt.PrintJobId.Value))
                    .OrderByDescending(attempt => attempt.AttemptNumber)
                    .ThenByDescending(attempt => attempt.ClaimedAtUtc)
                    .ToListAsync(cancellationToken);
                latestAttempts = attempts
                    .GroupBy(attempt => attempt.PrintJobId!.Value)
                    .ToDictionary(group => group.Key, group => group.First());
            }

            return jobs
                .Select(job =>
                {
                    QueuedPrintJobWithFileMetaDto dto =
                        MapToQueuedPrintJobWithFileMeta(
                            job,
                            dispatchVersions);
                    if (latestAttempts.TryGetValue(
                            job.Id,
                            out QueueDispatchAttempt? attempt))
                    {
                        string? dispatchVersion =
                            job.AssignedPrinterId is Guid printerId &&
                            dispatchVersions.TryGetValue(
                                printerId,
                                out string? version)
                                ? version
                                : null;
                        dto.Job.DispatchResult = QueueDispatchAttemptResultMapper.Map(
                            attempt,
                            job,
                            dispatchVersion);
                    }

                    return dto;
                })
                .ToList();
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
            if (!Guid.TryParse(printerId, out Guid printerIdGuid))
            {
                _logger.LogWarning("Invalid printer ID format: {PrinterId}", printerId);
                return [];
            }

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
    /// Get compact per-printer queue summaries used to derive the compact-card "X of Y" label
    /// for every printer in one call, replacing N per-printer <see cref="GetPrinterQueueAsync"/>
    /// round trips.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<List<PrinterQueueSummaryDto>> GetPrinterQueueSummariesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            List<PrinterQueueSummary> summaries = await _repository.GetPrinterQueueSummariesAsync(cancellationToken);
            return summaries
                .Select(s => new PrinterQueueSummaryDto(s.PrinterId, s.QueuedCount, s.PrintingCount, s.PrintingPosition))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving printer queue summaries");
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
            double avgWait = await _repository.GetAverageWaitTimeMinutesAsync(printerModelId: null, lookbackDays: 30, ct: cancellationToken);
            QueuePlanningSettings settings = GetQueuePlanningSettings();
            List<PrintJob> activeJobs = await _repository.GetFilteredJobsAsync(
                filterStatus: null,
                filterModel: null,
                filterMaterial: null,
                deadlineStartUtc: null,
                deadlineEndUtc: null,
                sortBy: "priority",
                limit: QueuePlanningMaxJobs,
                offset: 0,
                ct: cancellationToken);

            QueuePlanningProjection planning = BuildQueuePlanningProjection(activeJobs, settings, DateTime.UtcNow);

            return new QueueStatsDto
            {
                TotalQueued = queued,
                TotalPrinting = printing,
                TotalPaused = paused,
                AverageWaitTimeMinutes = (int)Math.Round(avgWait),
                EstimatedQueueCompletionUtc = planning.EstimatedQueueCompletionUtc,
                StaffedCompletionUtc = planning.StaffedCompletionUtc,
                Assumptions = new QueuePlanningAssumptionsDto
                {
                    WorkdayStartHourUtc = settings.WorkdayStartHourUtc,
                    WorkdayEndHourUtc = settings.WorkdayEndHourUtc,
                    BedClearMinutes = settings.BedClearMinutes,
                    DefaultDeadlineHours = settings.DefaultDeadlineHours,
                    RequireDeadline = settings.RequireDeadline,
                    MinimumLeadHours = settings.MinimumLeadHours
                }
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

            // Calculate per-model average wait times from recently completed jobs
            DateTime cutoff = DateTime.UtcNow.AddDays(-30);
            List<PrintJob> recentJobs = await _repository.GetCompletedJobsForAnalyticsAsync(
                dateFrom: cutoff, ct: cancellationToken);

            Dictionary<string, double> avgWaitByModel = recentJobs
                .Where(j => j.ActualStartTime.HasValue && j.AssignedPrinter?.Model?.Name != null)
                .GroupBy(j => j.AssignedPrinter!.Model!.Name)
                .ToDictionary(
                    g => g.Key,
                    g => g.Average(j => (j.ActualStartTime!.Value - j.QueuedAt).TotalMinutes));

            return stats.Select(s => new QueuePrinterModelStatsDto
            {
                ModelName = s.ModelName,
                TotalQueued = s.TotalQueued,
                CurrentlyPrinting = s.CurrentlyPrinting,
                OldestQueuedAtUtc = s.OldestQueuedAtUtc,
                AverageQueueWaitMinutes = avgWaitByModel.TryGetValue(s.ModelName, out double avg)
                    ? (int)Math.Round(avg) : 0
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving printer model statistics");
            throw;
        }
    }

    /// <summary>
    /// Derives an approximate completion percentage for a non-completed (failed/cancelled)
    /// history job from how far it progressed through its estimated print time. Returns 0
    /// when timing data is unavailable, and never returns 100 (reserved for completed jobs).
    /// </summary>
    private static int ComputePartialCompletionPercentage(TimeSpan? actualPrintTime, TimeSpan? estimatedPrintTime)
    {
        if (actualPrintTime is null || estimatedPrintTime is null || estimatedPrintTime.Value.TotalSeconds <= 0)
        {
            return 0;
        }

        double pct = actualPrintTime.Value.TotalSeconds / estimatedPrintTime.Value.TotalSeconds * 100.0;
        return (int)Math.Round(Math.Clamp(pct, 0, 99));
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
    /// <param name="deadlineStart">Optional inclusive lower bound for job deadlines.</param>
    /// <param name="deadlineEnd">Optional inclusive upper bound for job deadlines.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueueHistoryPageDto> GetQueueHistoryAsync(
        int limit = 50,
        int offset = 0,
        string sortBy = "completedAt",
        List<string>? statuses = null,
        DateTime? dateStart = null,
        DateTime? dateEnd = null,
        DateTime? deadlineStart = null,
        DateTime? deadlineEnd = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            (List<PrintJob> jobs, int totalCount, int completedCount, int failedCount, int cancelledCount, long totalPrintTimeSeconds) =
                await _repository.GetHistoryAsync(limit, offset, sortBy, statuses, dateStart, dateEnd, deadlineStart, deadlineEnd, cancellationToken);

            var entries = jobs
                .Select(pj => new QueueHistoryEntryDto
                {
                    Id = pj.Id.ToString(),
                    JobName = pj.GcodeFile?.Name ?? pj.Name,
                    PrinterName = pj.AssignedPrinter?.Name ?? "Unassigned",
                    Status = pj.Status.ToString(),
                    CompletionPercentage = pj.Status == PrintJobStatus.Completed
                        ? 100
                        : ComputePartialCompletionPercentage(pj.ActualPrintTime, pj.EstimatedPrintTime),
                    StartedAtUtc = pj.ActualStartTime ?? pj.CreatedAt,
                    CompletedAtUtc = pj.ActualEndTime,
                    DeadlineAtUtc = pj.DeadlineAtUtc,
                    ActualPrintTimeSeconds = (int?)pj.ActualPrintTime?.TotalSeconds ?? 0,
                    FailureReason = pj.FailureReason,
                    FilamentName = pj.FilamentName,
                    FilamentColor = pj.FilamentColor,
                    ActualFilamentUsageGrams = pj.ActualFilamentUsage,
                    EstimatedFilamentUsageGrams = pj.EstimatedFilamentUsage ?? (pj.GcodeFile != null ? pj.GcodeFile.EstimatedFilamentWeightG : null),
                    MaterialType = pj.RequiredMaterialType ?? (pj.GcodeFile != null ? pj.GcodeFile.RequiredMaterial : null),
                    ActualCost = pj.ActualCost,
                    MaterialCostUsd = pj.MaterialCostUsd,
                    TotalCostUsd = pj.TotalCostUsd,
                    CostIsEstimated = pj.ToolheadUsages.Count > 0
                        ? pj.ToolheadUsages.Any(tu => tu.SpoolmanSpoolId == null)
                        : pj.SpoolmanSpoolId == null,
                    ToolheadUsages = pj.ToolheadUsages
                        .OrderBy(tu => tu.ToolheadIndex)
                        .Select(tu => new PrintJobToolheadUsageDto(
                            tu.Id,
                            tu.PrintJobId,
                            tu.ToolheadIndex,
                            tu.SpoolmanSpoolId,
                            tu.FilamentUsageGrams,
                            tu.SlicerEstimateGrams,
                            tu.FilamentName,
                            tu.FilamentColor,
                            tu.MaterialCostUsd))
                        .ToList(),
                    Tags = pj.Tags
                        .Select(t => new TagDto
                        {
                            Id = t.Id,
                            Name = t.Name,
                            Category = t.Category,
                            IsAutoGenerated = t.IsAutoGenerated,
                            Color = t.Color,
                            Description = t.Description
                        })
                        .ToList(),
                    HarvestedAt = pj.HarvestedAt
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

            // SERVER-AUTHORITATIVE CLASSIFICATION (issue #900, defect 3).
            // The management/analytics enqueue path must never be able to queue a promoted
            // calibration artifact as a Standard job.
            QueueJobClassification classification = QueueJobClassifier.Classify(gcodeFile);
            if (classification.JobKind == JobKind.FilamentCalibration)
            {
                throw new ValidationException(
                    QueueJobClassifier.CalibrationMisclassificationMessage(gcodeFile.Id));
            }

            if (!QueueOrdering.IsDefinedPriority((int)request.Priority))
            {
                throw new ValidationException(
                    QueueOrdering.UndefinedPriorityMessage((int)request.Priority));
            }

            // Create new print job
            // Status is Assigned if a printer is specified, otherwise Queued
            Guid? assignedPrinterId = string.IsNullOrEmpty(request.AssignedPrinterId) ? null : Guid.Parse(request.AssignedPrinterId);
            if (assignedPrinterId.HasValue)
            {
                await EnsureActorCanAccessPrinterAsync(
                    userId,
                    assignedPrinterId.Value,
                    cancellationToken);
            }

            QueuePlanningSettings queuePlanningSettings = GetQueuePlanningSettings();
            DateTime? resolvedDeadlineAtUtc = ResolveEnqueueDeadline(request.DeadlineAtUtc, queuePlanningSettings);
            var job = new PrintJob
            {
                Id = Guid.NewGuid(),

                // Store a human-friendly name (original filename) for display.
                // The internal GUID-based filename is stored on the GcodeFile entity.
                Name = gcodeFile.Name,
                GcodeFileId = Guid.Parse(request.GcodeFileId),
                AssignedPrinterId = assignedPrinterId,
                Status = assignedPrinterId.HasValue ? PrintJobStatus.Assigned : PrintJobStatus.Queued,
                Priority = (int)request.Priority,
                RequiredNozzleDiameter = request.RequiredNozzleDiameter,
                RequiredMaterialType = Farm.Infrastructure.Services.PrintJobs.PrintJobRequirementsMapper
                    .ResolveEffectiveMaterial(request.RequiredMaterialType, gcodeFile),
                DeadlineAtUtc = resolvedDeadlineAtUtc,
                EstimatedPrintTime = gcodeFile.EstimatedPrintTimeMinutes.HasValue
                    ? TimeSpan.FromMinutes(gcodeFile.EstimatedPrintTimeMinutes.Value)
                    : null,
                EstimatedFilamentUsage = gcodeFile.EstimatedFilamentWeightG,
                JobKind = classification.JobKind,
                SourceArtifactId = classification.SourceArtifactId,
                SliceJobId = classification.SliceJobId,
                GcodeContentSha256 = classification.GcodeContentSha256 ?? gcodeFile.FileHash,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };

            // Calculate queue position
            job.QueuePosition = await AllocateQueuePositionAsync(
                assignedPrinterId,
                cancellationToken);

            if (assignedPrinterId.HasValue)
            {
                await AdvanceQueueRevisionAsync(
                    assignedPrinterId.Value,
                    "analytics queue insertion",
                    cancellationToken);
            }

            // Project per-extruder G-code metadata into per-tool material requirements
            // via the shared PrintJobRequirementsMapper so every enqueue path (this
            // service, JobQueueService, rerun) uses identical projection semantics.
            // Preserves RequiredMaterialType for legacy dispatch / reporting.
            Farm.Infrastructure.Services.PrintJobs.PrintJobRequirementsMapper.PopulateFromGcode(job, gcodeFile);

            if (_partOutputSnapshotService is null)
            {
                _ = await _repository.AddAsync(job, cancellationToken);
            }
            else
            {
                await _repository.AddWithoutSaveAsync(job, cancellationToken);
                if (assignedPrinterId.HasValue)
                {
                    await PrepareFirstAssignmentAsync(
                        job,
                        assignedPrinterId.Value,
                        userId,
                        "Assigned during enqueue.",
                        cancellationToken);
                }

                await _repository.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation("Print job {JobId} enqueued by user {UserId}", job.Id.ToString(), userId);

            // #709 item 5: queue mutation may change coverage (new assigned
            // demand added). Broadcast per-printer only when the job is bound;
            // unassigned shared-queue jobs affect no printer until assignment.
            if (_coverageBroadcaster is not null && job.AssignedPrinterId.HasValue)
            {
                await _coverageBroadcaster.BroadcastPrinterChangedAsync(
                    job.AssignedPrinterId.Value,
                    Farm.Infrastructure.Services.Spoolman.FilamentCoverageChangeReasons.JobAssignment,
                    cancellationToken).ConfigureAwait(false);
            }

            return MapToQueuedPrintJobDto(job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enqueueing print job from gcode file {GcodeFileId}", request.GcodeFileId);
            throw;
        }
    }

    /// <summary>
    /// Thin wrapper around
    /// <see cref="Farm.Infrastructure.Services.PrintJobs.PrintJobRequirementsMapper.PopulateFromGcode"/>
    /// kept for backward-compatible unit tests. New callers should invoke the shared
    /// mapper directly so every production entry point projects per-extruder metadata
    /// identically.
    /// </summary>
    /// <param name="job">The newly constructed print job to mutate.</param>
    /// <param name="gcodeFile">The G-code file supplying slicer metadata.</param>
    internal static void PopulatePerToolRequirementsFromGcode(PrintJob job, GcodeFile gcodeFile)
    {
        Farm.Infrastructure.Services.PrintJobs.PrintJobRequirementsMapper.PopulateFromGcode(job, gcodeFile);
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

            // Capture prior assignment/status so we can decide whether coverage
            // needs a rebroadcast after the update (#709 item 5).
            Guid? priorAssignedPrinterId = job.AssignedPrinterId;
            PrintJobStatus priorStatus = job.Status;

            await EnsureActorCanAccessJobAsync(userId, job.Id, cancellationToken);

            if (job.JobKind == JobKind.FilamentCalibration &&
                (!string.IsNullOrEmpty(request.AssignedPrinterId) ||
                 !string.IsNullOrEmpty(request.Status)))
            {
                throw new QueueSemanticConflictException(
                    "Calibration assignment and lifecycle fields are immutable on the compatibility update path.");
            }

            Guid? originalPrinterId = job.AssignedPrinterId;
            bool queueShapeChanged =
                request.Priority.HasValue ||
                !string.IsNullOrEmpty(request.AssignedPrinterId) ||
                !string.IsNullOrEmpty(request.Status);

            // Update fields if provided
            if (request.Priority.HasValue)
            {
                if (!QueueOrdering.IsDefinedPriority((int)request.Priority.Value))
                {
                    throw new ValidationException(
                        QueueOrdering.UndefinedPriorityMessage((int)request.Priority.Value));
                }

                job.Priority = (int)request.Priority.Value;
            }

            if (!string.IsNullOrEmpty(request.AssignedPrinterId))
            {
                Guid destinationPrinterId = Guid.Parse(request.AssignedPrinterId);
                await EnsureActorCanAccessPrinterAsync(
                    userId,
                    destinationPrinterId,
                    cancellationToken);
                if (priorAssignedPrinterId != destinationPrinterId)
                {
                    job.QueuePosition = await AllocateQueuePositionAsync(
                        destinationPrinterId,
                        cancellationToken);
                }

                job.AssignedPrinterId = destinationPrinterId;
                if (priorAssignedPrinterId != job.AssignedPrinterId)
                {
                    await PrepareFirstAssignmentAsync(
                        job,
                        job.AssignedPrinterId.Value,
                        userId,
                        "Assigned during queue update.",
                        cancellationToken);
                }
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

            if (request.DeadlineAtUtc.HasValue)
            {
                job.DeadlineAtUtc = ValidateProvidedDeadline(request.DeadlineAtUtc, GetQueuePlanningSettings());
            }

            job.UpdatedAt = DateTime.UtcNow;

            if (queueShapeChanged)
            {
                foreach (Guid printerId in new[] { originalPrinterId, job.AssignedPrinterId }
                             .Where(value => value.HasValue)
                             .Select(value => value!.Value)
                             .Distinct())
                {
                    await AdvanceQueueRevisionAsync(printerId, "analytics job update", cancellationToken);
                }
            }

            _ = await _repository.UpdateAsync(job, cancellationToken);
            _logger.LogInformation("Print job {JobId} updated by user {UserId}", jobId, userId);

            // #709 item 5: coverage changes when assignment moves or the
            // job's contribution to queued demand changes (status transitions
            // Queued/Assigned ↔ Printing/Completed/Cancelled). Broadcast on
            // any of these changes; the coalescer swallows repeated bursts.
            if (_coverageBroadcaster is not null)
            {
                bool assignmentChanged = job.AssignedPrinterId != priorAssignedPrinterId;
                bool statusChanged = job.Status != priorStatus;

                if (assignmentChanged || statusChanged)
                {
                    string reason = assignmentChanged
                        ? Farm.Infrastructure.Services.Spoolman.FilamentCoverageChangeReasons.JobAssignment
                        : Farm.Infrastructure.Services.Spoolman.FilamentCoverageChangeReasons.QueueChanged;

                    if (priorAssignedPrinterId.HasValue && priorAssignedPrinterId != job.AssignedPrinterId)
                    {
                        await _coverageBroadcaster.BroadcastPrinterChangedAsync(
                            priorAssignedPrinterId.Value, reason, cancellationToken).ConfigureAwait(false);
                    }

                    if (job.AssignedPrinterId.HasValue)
                    {
                        await _coverageBroadcaster.BroadcastPrinterChangedAsync(
                            job.AssignedPrinterId.Value, reason, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

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
        PrintJobPriority newPriority,
        string userId,
        CancellationToken cancellationToken = default) =>
        await UpdateJobPriorityAsync(
            jobId,
            newPriority,
            userId,
            ifMatchJobRowVersion: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<QueuedPrintJobDto> UpdateJobPriorityAsync(
        string jobId,
        PrintJobPriority newPriority,
        string userId,
        string? ifMatchJobRowVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
            if (job == null)
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            await EnsureActorCanAccessJobAsync(userId, job.Id, cancellationToken);
            if (ifMatchJobRowVersion is not null)
            {
                QueueRevisionGuard.EnsureIfMatch(
                    ifMatchJobRowVersion,
                    job.RowVersion,
                    "priority update");
            }

            if (!Enum.IsDefined(newPriority))
            {
                throw new ValidationException(
                    QueueOrdering.UndefinedPriorityMessage((int)newPriority));
            }

            job.Priority = (int)newPriority;
            job.UpdatedAt = DateTime.UtcNow;
            if (job.AssignedPrinterId.HasValue)
            {
                await AdvanceQueueRevisionAsync(
                    job.AssignedPrinterId.Value,
                    "analytics priority update",
                    cancellationToken);
            }

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
        CancellationToken cancellationToken = default) =>
        await PauseJobAsync(
            jobId,
            userId,
            ifMatchJobRowVersion: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<QueuedPrintJobDto> PauseJobAsync(
        string jobId,
        string userId,
        string? ifMatchJobRowVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
            if (job == null)
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            await EnsureActorCanAccessJobAsync(userId, job.Id, cancellationToken);
            QueueRevisionGuard.EnsureIfMatch(
                ifMatchJobRowVersion,
                job.RowVersion,
                "job pause");

            if (job.Status != PrintJobStatus.Printing)
            {
                throw new InvalidOperationException($"Only printing jobs can be paused. Current status: {job.Status}");
            }

            if (!job.AssignedPrinterId.HasValue || _appDbContext is null)
            {
                throw new InvalidOperationException(
                    "A durable pause command requires an assigned printer and queue database.");
            }

            await using QueueOutboxTransactionScope transaction =
                await QueueOutboxTransactionScope.BeginAsync(_appDbContext, cancellationToken);
            await EnqueueBackendControlCommandAsync(
                job,
                userId,
                operation: "pause",
                cancellationToken);
            await _repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation(
                "Durable pause command queued for job {JobId} by user {UserId}",
                jobId,
                userId);

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
        CancellationToken cancellationToken = default) =>
        await ResumeJobAsync(
            jobId,
            userId,
            ifMatchJobRowVersion: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<QueuedPrintJobDto> ResumeJobAsync(
        string jobId,
        string userId,
        string? ifMatchJobRowVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
            if (job == null)
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            await EnsureActorCanAccessJobAsync(userId, job.Id, cancellationToken);
            QueueRevisionGuard.EnsureIfMatch(
                ifMatchJobRowVersion,
                job.RowVersion,
                "job resume");

            if (job.Status != PrintJobStatus.Paused)
            {
                throw new InvalidOperationException($"Only paused jobs can be resumed. Current status: {job.Status}");
            }

            if (!job.AssignedPrinterId.HasValue || _appDbContext is null)
            {
                throw new InvalidOperationException(
                    "A durable resume command requires an assigned printer and queue database.");
            }

            await using QueueOutboxTransactionScope transaction =
                await QueueOutboxTransactionScope.BeginAsync(_appDbContext, cancellationToken);
            await EnqueueBackendControlCommandAsync(
                job,
                userId,
                operation: "resume",
                cancellationToken);
            await _repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation(
                "Durable resume command queued for job {JobId} by user {UserId}",
                jobId,
                userId);

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
        CancellationToken cancellationToken = default) =>
        await DispatchJobAsync(jobId, userId, ifMatchJobRowVersion: null, cancellationToken);

    /// <inheritdoc />
    public async Task<QueuedPrintJobDto> DispatchJobAsync(
        string jobId,
        string userId,
        string? ifMatchJobRowVersion,
        CancellationToken cancellationToken = default) =>
        await DispatchJobCoreAsync(
            jobId,
            userId,
            ifMatchJobRowVersion,
            expectedDispatchStateRowVersion: null,
            filamentOverride: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<QueuedPrintJobDto> DispatchReviewedJobAsync(
        string jobId,
        string userId,
        string ifMatchJobRowVersion,
        byte[] expectedDispatchStateRowVersion,
        FilamentOverrideAuthorization? filamentOverride,
        CancellationToken cancellationToken = default) =>
        await DispatchJobCoreAsync(
            jobId,
            userId,
            ifMatchJobRowVersion,
            expectedDispatchStateRowVersion,
            filamentOverride,
            cancellationToken);

    /// <inheritdoc />
    public async Task<QueuedPrintJobDto> DispatchJobWithFilamentOverrideAsync(
        string jobId,
        string userId,
        string ifMatchJobRowVersion,
        byte[] expectedDispatchStateRowVersion,
        FilamentOverrideAuthorization filamentOverride,
        CancellationToken cancellationToken = default) =>
        await DispatchJobCoreAsync(
            jobId,
            userId,
            ifMatchJobRowVersion,
            expectedDispatchStateRowVersion,
            filamentOverride,
            cancellationToken);

    private async Task<QueuedPrintJobDto> DispatchJobCoreAsync(
        string jobId,
        string userId,
        string? ifMatchJobRowVersion,
        byte[]? expectedDispatchStateRowVersion,
        FilamentOverrideAuthorization? filamentOverride,
        CancellationToken cancellationToken)
    {
        PrintJob? dispatchJob = null;
        QueueDispatchAttempt? dispatchAttempt = null;
        try
        {
            // Load job with related entities
            dispatchJob = await _repository.GetByIdWithRelationsAsync(
                Guid.Parse(jobId),
                cancellationToken);

            if (dispatchJob == null)
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            PrintJob job = dispatchJob;
            await EnsureActorCanAccessJobAsync(userId, job.Id, cancellationToken);

            byte[]? expectedJobRowVersion = QueueRevisionGuard.DecodeIfMatch(
                ifMatchJobRowVersion,
                "job dispatch");
            if (expectedJobRowVersion is not null &&
                !expectedJobRowVersion.SequenceEqual(job.RowVersion ?? []))
            {
                byte[]? currentDispatchRevision = job.AssignedPrinterId.HasValue &&
                                                  _appDbContext is not null
                    ? await _appDbContext.PrinterDispatchStates
                        .AsNoTracking()
                        .Where(state => state.PrinterId == job.AssignedPrinterId.Value)
                        .Select(state => state.RowVersion)
                        .SingleOrDefaultAsync(cancellationToken)
                    : null;
                throw new QueueRevisionConflictException(
                    "The resource has changed since the request was prepared (job dispatch). " +
                    "Re-fetch the ETag and retry.",
                    job.RowVersion,
                    currentDispatchRevision);
            }

            // Idempotent: if the job is already being dispatched (e.g. by auto-dispatch),
            // return its current state as success rather than erroring out.
            if (job.Status is PrintJobStatus.Starting or PrintJobStatus.Printing or PrintJobStatus.Paused)
            {
                _logger.LogInformation(
                    "Job {JobId} already in {Status} state — returning current state (idempotent dispatch)",
                    jobId, job.Status);
                return await AttachLatestDispatchResultAsync(
                    MapToQueuedPrintJobDto(job),
                    job,
                    cancellationToken);
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

            if (!job.AssignedPrinter.IsEnabled)
            {
                throw new InvalidOperationException("Cannot dispatch a job to a disabled printer.");
            }

            // Validate G-code file exists
            if (job.GcodeFile == null)
            {
                throw new InvalidOperationException($"G-code file not found for job {jobId}");
            }

            if (_dispatchClaimService is null)
            {
                throw new InvalidOperationException(
                    "IDispatchClaimService is required for dispatch. This service must be registered in the DI container.");
            }

            string startPathKind = filamentOverride?.OverrideApproved == true
                ? "FilamentOverride"
                : expectedDispatchStateRowVersion is not null
                    ? "ReadyConfirmation"
                    : "Manual";
            DispatchClaimResult claimResult = await _dispatchClaimService.AcquireClaimAsync(
                new DispatchClaimRequest(
                    Guid.Parse(jobId),
                    job.AssignedPrinterId.Value,
                    userId,
                    startPathKind,
                    null,
                    expectedJobRowVersion,
                    expectedDispatchStateRowVersion,
                    filamentOverride),
                cancellationToken);

            if (!claimResult.Success || claimResult.Attempt is null)
            {
                if (claimResult.CurrentFilamentCheck is not null &&
                    claimResult.CurrentFilamentCheckVersion is { Length: > 0 })
                {
                    throw new FilamentCheckChangedException(
                        claimResult.CurrentFilamentCheck,
                        claimResult.CurrentFilamentCheckVersion);
                }

                if (claimResult.IsPreconditionFailure)
                {
                    throw new QueueRevisionConflictException(
                        $"{claimResult.ErrorCode} {claimResult.ErrorDetail}".Trim(),
                        claimResult.CurrentJobRowVersion,
                        claimResult.CurrentDispatchStateRowVersion);
                }

                throw new InvalidOperationException($"{claimResult.ErrorCode} {claimResult.ErrorDetail}".Trim());
            }

            dispatchAttempt = claimResult.Attempt;
            Guid? dispatchAttemptId = dispatchAttempt.Id;
            int dispatchAttemptNumber = claimResult.Attempt.AttemptNumber;
            long uploadProgressSequence = 0;
            string? dispatchJobRevision = job.RowVersion is { Length: > 0 }
                ? Convert.ToBase64String(job.RowVersion)
                : null;
            string? dispatchStateRevision =
                claimResult.Attempt.DispatchStateRowVersionAtClaim is { Length: > 0 }
                    ? Convert.ToBase64String(
                        claimResult.Attempt.DispatchStateRowVersionAtClaim)
                    : null;

            // Preserve Epic #705 harvest provenance now that the shared atomic claim has
            // performed the durable Starting transition: capture the idempotent part-output
            // snapshot and record the manual dispatch intent. These changes are tracked on
            // the shared context and persisted by the terminal SaveChangesAsync below.
            await PrepareFirstAssignmentAsync(
                job,
                job.AssignedPrinterId.Value,
                userId,
                "Dispatched to start printing.",
                cancellationToken);

            // Use original filename for the printer (not the GUID-based storage filename)
            string printerFileName = claimResult.Attempt.BackendFileName
                ?? throw new InvalidOperationException("Dispatch claim did not persist a backend file identity.");

            // Resolve the full local file path: StorageRoot + VirtualPath + FileName
            string gcodeStorageRoot = _storagePathService.GetGcodeStorageDirectory();
            string localFilePath = Path.Combine(gcodeStorageRoot, job.GcodeFile.FilePath.TrimStart('/'), job.GcodeFile.FileName);

            _logger.LogInformation(
                "Dispatching print job {JobId} to printer {PrinterId}: uploading {OriginalName}",
                jobId, job.AssignedPrinterId, printerFileName);

            try
            {
                // Validate the local file exists
                if (!System.IO.File.Exists(localFilePath))
                {
                    job.FailureReason = DispatchArtifactUnavailable;
                    bool applied = await _dispatchClaimService.ReleaseClaimOnKnownFailureAsync(
                        dispatchAttemptId.Value,
                        "backend_rejected",
                        DispatchArtifactUnavailable,
                        cancellationToken);

                    _logger.LogError(
                        "G-code artifact is unavailable for print job {JobId}",
                        jobId);
                    return applied
                        ? await BuildDispatchResultAsync(
                            job,
                            claimResult.Attempt,
                            cancellationToken)
                        : BuildSupersededDispatchResult(job, claimResult.Attempt);
                }
                else
                {
                    // Step 1: Upload the file to the printer
                    await using FileStream fileStream = System.IO.File.OpenRead(localFilePath);
                    if (job.JobKind == JobKind.FilamentCalibration)
                    {
                        StoredGcodeIntegrityResult uploadIntegrity =
                            await StoredGcodeIntegrityVerifier.VerifyOpenedStreamAsync(
                                fileStream,
                                job.GcodeContentSha256 ?? string.Empty,
                                job.PinnedGcodeFileSizeBytes,
                                cancellationToken);
                        if (!uploadIntegrity.Success)
                        {
                            job.FailureReason = DispatchArtifactUnavailable;
                            bool applied =
                                await _dispatchClaimService.ReleaseClaimOnKnownFailureAsync(
                                dispatchAttemptId.Value,
                                uploadIntegrity.ErrorCode ?? "gcode_byte_hash_mismatch",
                                uploadIntegrity.ErrorDetail ?? DispatchArtifactUnavailable,
                                cancellationToken);
                            return applied
                                ? await BuildDispatchResultAsync(
                                    job,
                                    claimResult.Attempt,
                                    cancellationToken)
                                : BuildSupersededDispatchResult(
                                    job,
                                    claimResult.Attempt);
                        }
                    }

                    long totalBytes = 0;
                    try
                    {
                        totalBytes = fileStream.Length;
                    }
                    catch
                    {
                        // best-effort: Length may not be available
                    }

                    long lastReportedBytes = 0;
                    long lastReportAt = Stopwatch.GetTimestamp();
                    var reportInterval = TimeSpan.FromMilliseconds(500);
                    const long ReportEveryBytes = 512 * 1024; // 512KB

                    // Stage progress: backends report which step they're on (uploading, processing, starting).
                    string? currentStage = null;
                    var stageProgress = new Progress<UploadAndPrintStage>(stage =>
                    {
                        currentStage = stage.ToString();
                        _logger.LogDebug("Print job {JobId} stage: {Stage}", jobId, currentStage);
                    });

                    async Task ReportProgressAsync(long bytesSent, bool force)
                    {
                        if (totalBytes <= 0)
                        {
                            return;
                        }

                        long now = Stopwatch.GetTimestamp();
                        TimeSpan sinceLastReport = Stopwatch.GetElapsedTime(lastReportAt, now);
                        bool hasMeaningfulDelta = bytesSent - lastReportedBytes >= ReportEveryBytes;
                        bool intervalElapsed = sinceLastReport >= reportInterval;

                        if (!force && !hasMeaningfulDelta && !intervalElapsed)
                        {
                            return;
                        }

                        lastReportedBytes = bytesSent;
                        lastReportAt = now;

                        var dto = new DispatchUploadProgressDto
                        {
                            JobId = jobId,
                            AttemptId = dispatchAttemptId.Value,
                            AttemptNumber = dispatchAttemptNumber,
                            Sequence = Interlocked.Increment(
                                ref uploadProgressSequence),
                            JobRevision = dispatchJobRevision,
                            DispatchStateRevision = dispatchStateRevision,
                            PrinterId = job.AssignedPrinterId.Value.ToString(),
                            FileName = printerFileName,
                            BytesSent = Math.Min(bytesSent, totalBytes),
                            TotalBytes = totalBytes,
                            IsCompleted = force && bytesSent >= totalBytes,
                            IsFailed = false,
                            Stage = currentStage,
                        };

                        await _hubContext.Clients.Group(
                            Farm.Infrastructure.Security.AuthorizedHubGroups.QueueJob(job.Id))
                            .SendAsync("dispatchuploadprogress", dto, cancellationToken);
                    }

                    // Emit a 0% snapshot so the UI can immediately show progress.
                    await ReportProgressAsync(0, force: true);

                    using var progressStream = new ProgressReportingStream(
                        fileStream,
                        bytesSent => ReportProgressAsync(bytesSent, force: false));

                    // All backends implement ISupportsUploadAndPrint, handling protocol-specific
                    // delays, path resolution, and retries internally.
                    bool callStarted =
                        await _dispatchClaimService.RecordBackendCallStartedAsync(
                            dispatchAttemptId.Value,
                            cancellationToken);
                    if (!callStarted)
                    {
                        return BuildSupersededDispatchResult(
                            job,
                            claimResult.Attempt);
                    }

                    var result = await _printersService.UploadAndStartPrintAsync(
                        job.AssignedPrinterId.Value,
                        printerFileName,
                        progressStream,
                        stageProgress,
                        cancellationToken);

                    if (result.Success)
                    {
                        // Emit a forced 100% snapshot.
                        if (totalBytes > 0)
                        {
                            await ReportProgressAsync(totalBytes, force: true);
                        }

                        bool applied =
                            await _dispatchClaimService.RecordBackendAcceptedAsync(
                            dispatchAttemptId.Value,
                            result.BackendJobId,
                            result.BackendFileIdentity ?? printerFileName,
                            cancellationToken);
                        if (!applied)
                        {
                            return BuildSupersededDispatchResult(
                                job,
                                claimResult.Attempt);
                        }

                        try
                        {
                            // This enrichment is post-accept work. It may fail without changing
                            // the physical fact that the backend accepted the print.
                            await SnapshotSlicerEstimatesAsync(job, cancellationToken);
                        }
                        catch (Exception enrichmentException)
                        {
                            _logger.LogWarning(
                                enrichmentException,
                                "Post-accept slicer estimate snapshot failed for job {JobId}",
                                jobId);
                        }

                        _ = await _dispatchClaimService.RecordPostAcceptCompletedAsync(
                            dispatchAttemptId.Value,
                            CancellationToken.None);

                        _logger.LogInformation("Print job {JobId} successfully uploaded and started on printer {PrinterId}", jobId, job.AssignedPrinterId);
                    }
                    else if (result.Outcome == UploadAndPrintOutcome.Unknown)
                    {
                        bool applied =
                            await _dispatchClaimService.RecordUnknownOutcomeAsync(
                            dispatchAttemptId.Value,
                            result.ErrorMessage ?? DispatchUnexpectedFailure,
                            cancellationToken);
                        if (!applied)
                        {
                            return BuildSupersededDispatchResult(
                                job,
                                claimResult.Attempt);
                        }

                        job.FailureReason = DispatchUnexpectedFailure;

                        _logger.LogWarning(
                            "Upload/start outcome is unknown for job {JobId} on printer {PrinterId}; lease retained",
                            jobId,
                            job.AssignedPrinterId);
                    }
                    else
                    {
                        job.FailureReason = DispatchPrinterFailure;
                        string failureDetail = result.ErrorMessage ?? DispatchPrinterFailure;
                        bool applied =
                            await _dispatchClaimService.ReleaseClaimOnKnownFailureAsync(
                            dispatchAttemptId.Value,
                            "backend_rejected",
                            failureDetail,
                            cancellationToken);

                        _logger.LogWarning(
                            "Failed to upload and start print job {JobId} on printer {PrinterId} at stage {Stage}",
                            jobId, job.AssignedPrinterId, result.FailedStage);

                        // Best-effort: notify completion state so UI can stop showing upload progress.
                        if (totalBytes > 0)
                        {
                            var failedProgress = new DispatchUploadProgressDto
                            {
                                JobId = jobId,
                                AttemptId = dispatchAttemptId.Value,
                                AttemptNumber = dispatchAttemptNumber,
                                Sequence = Interlocked.Increment(
                                    ref uploadProgressSequence),
                                JobRevision = dispatchJobRevision,
                                DispatchStateRevision = dispatchStateRevision,
                                PrinterId = job.AssignedPrinterId.Value.ToString(),
                                FileName = printerFileName,
                                BytesSent = lastReportedBytes,
                                TotalBytes = totalBytes,
                                IsCompleted = true,
                                IsFailed = true,
                                Stage = result.FailedStage.ToString(),
                                ErrorMessage = DispatchPrinterFailure,
                            };
                            await _hubContext.Clients.Group(
                                Farm.Infrastructure.Security.AuthorizedHubGroups.QueueJob(job.Id))
                                .SendAsync(
                                    "dispatchuploadprogress",
                                    failedProgress,
                                    cancellationToken);
                        }

                        return applied
                            ? await BuildDispatchResultAsync(
                                job,
                                claimResult.Attempt,
                                cancellationToken)
                            : BuildSupersededDispatchResult(
                                job,
                                claimResult.Attempt);
                    }
                }
            }
            catch (Exception printEx)
            {
                DispatchExceptionDisposition disposition =
                    await _dispatchClaimService.RecordDispatchExceptionAsync(
                    dispatchAttemptId.Value,
                    "dispatch_exception",
                    CancellationToken.None);
                if (disposition == DispatchExceptionDisposition.Superseded)
                {
                    return BuildSupersededDispatchResult(
                        job,
                        claimResult.Attempt);
                }

                if (disposition == DispatchExceptionDisposition.ReleasedBeforeStart)
                {
                    job.FailureReason = DispatchPrinterFailure;
                }
                else if (disposition == DispatchExceptionDisposition.AwaitingReconciliation)
                {
                    job.FailureReason = DispatchUnexpectedFailure;
                }

                _logger.LogError(
                    printEx,
                    "Error dispatching print job {JobId} to printer {PrinterId}; exception type {ExceptionType}",
                    jobId,
                    job.AssignedPrinterId,
                    printEx.GetType().Name);
            }

            job.UpdatedAt = DateTime.UtcNow;
            await _repository.SaveChangesAsync(cancellationToken);

            if (job.Status == PrintJobStatus.Printing
                && job.AssignedPrinterId.HasValue
                && _coverageBroadcaster is not null)
            {
                await _coverageBroadcaster.BroadcastPrinterChangedAsync(
                    job.AssignedPrinterId.Value,
                    Farm.Infrastructure.Services.Spoolman.FilamentCoverageChangeReasons.JobAssignment,
                    cancellationToken).ConfigureAwait(false);
            }

            // Send notification for job start
            if (job.Status == PrintJobStatus.Printing)
            {
                await SendJobStartNotificationAsync(job, cancellationToken);

                // Capture camera snapshot on print start (true fire-and-forget in background scope)
                if (_cameraSnapshotService is not null && _serviceScopeFactory is not null && job.AssignedPrinterId.HasValue)
                {
                    Guid captureForPrinter = job.AssignedPrinterId.Value;
                    Guid captureForJob = job.Id;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using IServiceScope scope = _serviceScopeFactory.CreateScope();
                            ICameraSnapshotService svc = scope.ServiceProvider.GetRequiredService<ICameraSnapshotService>();
                            await svc.CaptureSnapshotAsync(captureForPrinter, "PrintStarted", captureForJob, CancellationToken.None);
                        }
                        catch (Exception snapEx)
                        {
                            _logger.LogWarning(
                                snapEx,
                                "[PrintJobManagementService] Background snapshot capture failed for printer {PrinterId}",
                                captureForPrinter);
                        }
                    });
                }

                // Fire-and-forget: query Moonraker for fresh state and broadcast via SignalR.
                // This eliminates the UI delay when the subscription is in HTTP polling fallback mode.
                if (_printerStatusRefreshService is not null && job.AssignedPrinterId.HasValue)
                {
                    _ = _printerStatusRefreshService.RefreshPrinterStatusAsync(
                        job.AssignedPrinterId.Value, delayMs: 750, CancellationToken.None);
                }
            }

            QueuedPrintJobDto response = MapToQueuedPrintJobDto(job);
            response.DispatchResult = await MapDispatchAttemptResultAsync(
                claimResult.Attempt,
                job,
                cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Error dispatching print job {JobId}; exception type {ExceptionType}",
                jobId,
                ex.GetType().Name);
            if (dispatchJob is not null &&
                dispatchAttempt is not null &&
                _dispatchClaimService is not null)
            {
                DispatchExceptionDisposition disposition =
                    await _dispatchClaimService.RecordDispatchExceptionAsync(
                        dispatchAttempt.Id,
                        "dispatch_exception",
                        CancellationToken.None);
                if (disposition != DispatchExceptionDisposition.Superseded)
                {
                    return await AttachLatestDispatchResultAsync(
                        MapToQueuedPrintJobDto(dispatchJob),
                        dispatchJob,
                        CancellationToken.None);
                }

                return BuildSupersededDispatchResult(
                    dispatchJob,
                    dispatchAttempt);
            }

            throw;
        }
    }

    private async Task PrepareFirstAssignmentAsync(
        PrintJob job,
        Guid printerId,
        string? userId,
        string reason,
        CancellationToken ct)
    {
        if (_partOutputSnapshotService is null)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        job.DispatchedAt ??= now;
        job.DispatchMode ??= (int)Farm.Infrastructure.Services.Queue.Dispatch.DispatchMode.Manual;
        _ = await _partOutputSnapshotService.CaptureJobSnapshotIfAbsentAsync(job, ct);
        _repository.AddDispatchLog(new DispatchLog
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            PrinterId = printerId,
            Action = Farm.Infrastructure.Services.Queue.Dispatch.DispatchAction.Dispatched,
            DispatchMode = Farm.Infrastructure.Services.Queue.Dispatch.DispatchMode.Manual,
            DispatchedAt = new DateTimeOffset(now, TimeSpan.Zero),
            DispatchedByUserId = userId,
            Reason = reason,
            CreatedAtUtc = now,
        });
    }

    /// <summary>
    /// Dispatches a job using an explicit bed-clear acknowledgement key.
    /// Called by the outbox publisher's BackendStartCommand handler to drive the
    /// durable bed-clear start path through the shared dispatch claim service.
    /// </summary>
    public async Task<BackendStartOutcome> DispatchJobWithAckAsync(
        string jobId,
        string actorSubject,
        string ackKey,
        CancellationToken cancellationToken = default)
    {
        if (_dispatchClaimService is null)
        {
            throw new InvalidOperationException(
                "IDispatchClaimService is required for DispatchJobWithAckAsync. Register the service in DI.");
        }

        PrintJob? job = await _repository.GetByIdWithRelationsAsync(Guid.Parse(jobId), cancellationToken);

        if (job is null)
        {
            return BackendStartOutcome.Rejected(
                "job_not_found", $"Print job {jobId} not found.");
        }

        QueueDispatchAttempt? resumableAttempt = null;

        // Database state alone is not proof that the backend accepted the command.
        // Starting always requires reconciliation. Printing is a safe no-op only when
        // the active attempt has a persisted Accepted outcome.
        if (job.Status is PrintJobStatus.Starting or PrintJobStatus.Printing or PrintJobStatus.Paused)
        {
            QueueDispatchAttempt? persistedAttempt = _appDbContext is null
                ? null
                : await _appDbContext.QueueDispatchAttempts
                    .Where(attempt => attempt.PrintJobId == job.Id)
                    .OrderByDescending(attempt => attempt.ClaimedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);

            PrinterDispatchState? persistedState =
                _appDbContext is null || persistedAttempt is null
                ? null
                : await _appDbContext.PrinterDispatchStates
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        state => state.ActiveJobId == job.Id &&
                                 state.ActiveDispatchAttemptId == persistedAttempt.Id,
                        cancellationToken);
            bool commandMatches = persistedAttempt is not null &&
                _appDbContext is not null &&
                await _appDbContext.BedClearCommandRecords
                    .AsNoTracking()
                    .AnyAsync(
                        command =>
                            command.JobId == job.Id &&
                            command.DispatchAttemptId == persistedAttempt.Id &&
                            command.IdempotencyKey == ackKey &&
                            command.Status == BedClearCommandStatus.Claimed,
                        cancellationToken);

            if (job.Status == PrintJobStatus.Starting &&
                persistedAttempt?.Outcome == DispatchAttemptOutcome.InProgress &&
                persistedAttempt.BackendCallPhase == DispatchBackendCallPhase.PreCall &&
                persistedState is not null &&
                commandMatches)
            {
                resumableAttempt = persistedAttempt;
            }

            if (resumableAttempt is null &&
                job.Status == PrintJobStatus.Printing &&
                persistedAttempt?.Outcome == DispatchAttemptOutcome.Accepted)
            {
                return BackendStartOutcome.AlreadyStarted(
                    "The backend acceptance was already persisted.",
                    persistedAttempt.Id,
                    backendAcceptanceProven: true);
            }

            if (resumableAttempt is null)
            {
                return BackendStartOutcome.Unknown(
                    $"Job is {job.Status}, but backend acceptance has not been proven.",
                    persistedAttempt?.Id);
            }
        }

        if (resumableAttempt is null &&
            job.Status is not (PrintJobStatus.Queued or PrintJobStatus.Assigned))
        {
            return BackendStartOutcome.Rejected(
                "job_not_dispatchable",
                $"Job {jobId} is in state {job.Status} and cannot be dispatched.");
        }

        if (job.AssignedPrinterId is null || job.AssignedPrinter is null)
        {
            return BackendStartOutcome.Rejected(
                "printer_not_assigned", $"Job {jobId} has no assigned printer.");
        }

        if (job.GcodeFile is null)
        {
            return BackendStartOutcome.Rejected(
                "gcode_missing", $"Job {jobId} has no G-code artifact.");
        }

        // Acquire the shared dispatch claim. This validates the persisted ack against
        // ackKey, checks telemetry, firmware, slicer compatibility, and sets Starting.
        DispatchClaimResult? claimResult = null;
        QueueDispatchAttempt? dispatchAttempt = resumableAttempt;
        if (dispatchAttempt is null)
        {
            claimResult = await _dispatchClaimService.AcquireClaimAsync(
                new DispatchClaimRequest(
                    Guid.Parse(jobId),
                    job.AssignedPrinterId.Value,
                    actorSubject,
                    "BedClear",
                    ackKey,
                    null,
                    null),
                cancellationToken);

            if (!claimResult.Success || claimResult.Attempt is null)
            {
                _logger.LogWarning(
                    "DispatchJobWithAckAsync: Claim denied for job {JobId} — {Code}",
                    jobId, claimResult.ErrorCode);

                // Concurrency conflicts and transient telemetry gaps may clear on retry;
                // every other guard denial is deterministic and must not be retried blindly.
                bool transient = claimResult.ErrorCode is
                    "concurrency_conflict" or "telemetry_unavailable" or "telemetry_stale" or
                    "printer_busy_telemetry" or "printer_offline";

                return BackendStartOutcome.Rejected(
                    claimResult.ErrorCode ?? "claim_denied",
                    claimResult.ErrorDetail ?? "Claim denied.",
                    isRetryable: transient);
            }

            dispatchAttempt = claimResult.Attempt;
        }

        Guid attemptId = dispatchAttempt.Id;

        string printerFileName = dispatchAttempt.BackendFileName
            ?? throw new InvalidOperationException("Dispatch claim did not persist a backend file identity.");
        string gcodeStorageRoot = _storagePathService.GetGcodeStorageDirectory();
        string localFilePath = Path.Combine(
            gcodeStorageRoot,
            job.GcodeFile.FilePath.TrimStart('/'),
            job.GcodeFile.FileName);

        _logger.LogInformation(
            "DispatchJobWithAckAsync: Uploading job {JobId} to printer {PrinterId}",
            jobId, job.AssignedPrinterId.Value);

        try
        {
            if (!System.IO.File.Exists(localFilePath))
            {
                bool applied = await _dispatchClaimService.ReleaseClaimOnKnownFailureAsync(
                    attemptId, "backend_rejected", DispatchArtifactUnavailable, cancellationToken);

                _logger.LogError(
                    "DispatchJobWithAckAsync: G-code artifact unavailable for job {JobId}",
                    jobId);

                return applied
                    ? BackendStartOutcome.FailedBeforeStart(
                        "artifact_unavailable",
                        DispatchArtifactUnavailable,
                        attemptId)
                    : SupersededBackendStart(attemptId);
            }

            await using FileStream fileStream = System.IO.File.OpenRead(localFilePath);
            if (job.JobKind == JobKind.FilamentCalibration)
            {
                StoredGcodeIntegrityResult uploadIntegrity =
                    await StoredGcodeIntegrityVerifier.VerifyOpenedStreamAsync(
                        fileStream,
                        job.GcodeContentSha256 ?? string.Empty,
                        job.PinnedGcodeFileSizeBytes,
                        cancellationToken);
                if (!uploadIntegrity.Success)
                {
                    bool applied =
                        await _dispatchClaimService.ReleaseClaimOnKnownFailureAsync(
                        attemptId,
                        uploadIntegrity.ErrorCode ?? "gcode_byte_hash_mismatch",
                        uploadIntegrity.ErrorDetail ?? DispatchArtifactUnavailable,
                        cancellationToken);
                    return applied
                        ? BackendStartOutcome.FailedBeforeStart(
                            uploadIntegrity.ErrorCode ?? "gcode_byte_hash_mismatch",
                            uploadIntegrity.ErrorDetail ?? DispatchArtifactUnavailable,
                            attemptId)
                        : SupersededBackendStart(attemptId);
                }
            }

            long totalBytes = fileStream.Length;
            long progressSequence = 0;
            long lastReportedBytes = 0;
            async Task ReportBedClearProgressAsync(
                long bytesSent,
                bool completed,
                bool failed,
                string? stage)
            {
                lastReportedBytes = Math.Min(bytesSent, totalBytes);
                await _hubContext.Clients.Group(
                        Farm.Infrastructure.Security.AuthorizedHubGroups.QueueJob(job.Id))
                    .SendAsync(
                        "dispatchuploadprogress",
                        new DispatchUploadProgressDto
                        {
                            JobId = jobId,
                            AttemptId = attemptId,
                            AttemptNumber = dispatchAttempt.AttemptNumber,
                            Sequence = Interlocked.Increment(ref progressSequence),
                            JobRevision = job.RowVersion is { Length: > 0 }
                                ? Convert.ToBase64String(job.RowVersion)
                                : null,
                            DispatchStateRevision =
                                dispatchAttempt.DispatchStateRowVersionAtClaim is
                                { Length: > 0 }
                                    ? Convert.ToBase64String(
                                        dispatchAttempt.DispatchStateRowVersionAtClaim)
                                    : null,
                            PrinterId = job.AssignedPrinterId.Value.ToString(),
                            FileName = printerFileName,
                            BytesSent = lastReportedBytes,
                            TotalBytes = totalBytes,
                            IsCompleted = completed,
                            IsFailed = failed,
                            Stage = stage,
                            ErrorMessage = failed ? DispatchPrinterFailure : null,
                        },
                        cancellationToken);
            }

            await ReportBedClearProgressAsync(
                0,
                completed: false,
                failed: false,
                stage: "Uploading");
            using var progressStream = new ProgressReportingStream(
                fileStream,
                bytesSent => ReportBedClearProgressAsync(
                    bytesSent,
                    completed: false,
                    failed: false,
                    stage: "Uploading"));
            var stageProgress = new Progress<UploadAndPrintStage>(stage =>
                _logger.LogDebug("DispatchJobWithAckAsync: Job {JobId} stage {Stage}", jobId, stage));

            bool callStarted =
                await _dispatchClaimService.RecordBackendCallStartedAsync(
                    attemptId,
                    cancellationToken);
            if (!callStarted)
            {
                return SupersededBackendStart(attemptId);
            }

            var uploadResult = await _printersService.UploadAndStartPrintAsync(
                job.AssignedPrinterId.Value,
                printerFileName,
                progressStream,
                stageProgress,
                cancellationToken);

            if (uploadResult.Success)
            {
                await ReportBedClearProgressAsync(
                    totalBytes,
                    completed: true,
                    failed: false,
                    stage: "Accepted");
                bool applied =
                    await _dispatchClaimService.RecordBackendAcceptedAsync(
                    attemptId, uploadResult.BackendJobId, cancellationToken);
                if (!applied)
                {
                    return SupersededBackendStart(attemptId);
                }

                try
                {
                    await SnapshotSlicerEstimatesAsync(job, cancellationToken);
                }
                catch (Exception enrichmentException)
                {
                    _logger.LogWarning(
                        enrichmentException,
                        "Post-accept slicer estimate snapshot failed for job {JobId}",
                        jobId);
                }

                _ = await _dispatchClaimService.RecordPostAcceptCompletedAsync(
                    attemptId,
                    CancellationToken.None);

                _logger.LogInformation(
                    "DispatchJobWithAckAsync: Job {JobId} successfully started on printer {PrinterId}",
                    jobId, job.AssignedPrinterId.Value);

                return BackendStartOutcome.Accepted(attemptId);
            }

            string failureDetail = uploadResult.ErrorMessage ?? DispatchPrinterFailure;
            if (uploadResult.Outcome == UploadAndPrintOutcome.Unknown)
            {
                await ReportBedClearProgressAsync(
                    lastReportedBytes,
                    completed: true,
                    failed: true,
                    stage: "Unknown");
                bool applied =
                    await _dispatchClaimService.RecordUnknownOutcomeAsync(
                    attemptId,
                    failureDetail,
                    cancellationToken);

                return applied
                    ? BackendStartOutcome.Unknown(
                        "The backend outcome could not be determined; reconciliation is required.",
                        attemptId)
                    : SupersededBackendStart(attemptId);
            }

            bool released =
                await _dispatchClaimService.ReleaseClaimOnKnownFailureAsync(
                attemptId, "backend_rejected", failureDetail, cancellationToken);
            await ReportBedClearProgressAsync(
                lastReportedBytes,
                completed: true,
                failed: true,
                stage: uploadResult.FailedStage.ToString());
            if (!released)
            {
                return SupersededBackendStart(attemptId);
            }

            _logger.LogWarning(
                "DispatchJobWithAckAsync: Backend rejected job {JobId} — {Failure}",
                jobId, failureDetail);

            return uploadResult.Outcome == UploadAndPrintOutcome.FailedBeforeStart
                ? BackendStartOutcome.FailedBeforeStart(
                    "backend_failed_before_start",
                    DispatchPrinterFailure,
                    attemptId,
                    isRetryable: false)
                : BackendStartOutcome.Rejected(
                    "backend_rejected",
                    DispatchPrinterFailure,
                    attemptId,
                    isRetryable: false);
        }
        catch (OperationCanceledException)
        {
            DispatchExceptionDisposition disposition =
                await _dispatchClaimService.RecordDispatchExceptionAsync(
                    attemptId,
                    "dispatch_cancelled",
                    CancellationToken.None);
            return MapDispatchException(disposition, attemptId);
        }
        catch (Exception ex)
        {
            DispatchExceptionDisposition disposition =
                await _dispatchClaimService.RecordDispatchExceptionAsync(
                    attemptId,
                    "dispatch_exception",
                    CancellationToken.None);

            _logger.LogError(
                ex,
                "DispatchJobWithAckAsync: Unknown outcome for job {JobId} on printer {PrinterId}",
                jobId,
                job.AssignedPrinterId.Value);

            return MapDispatchException(disposition, attemptId);
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
        CancellationToken cancellationToken = default) =>
        await CancelJobAsync(jobId, userId, ifMatchJobRowVersion: null, cancellationToken);

    /// <inheritdoc />
    public async Task CancelJobAsync(
        string jobId,
        string userId,
        string? ifMatchJobRowVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
            if (job == null)
            {
                throw new InvalidOperationException($"Print job {jobId} not found");
            }

            await EnsureActorCanAccessJobAsync(userId, job.Id, cancellationToken);

            QueueRevisionGuard.EnsureIfMatch(ifMatchJobRowVersion, job.RowVersion, "job cancellation");

            if (job.Status == PrintJobStatus.Completed || job.Status == PrintJobStatus.Cancelled)
            {
                throw new QueueSemanticConflictException($"Cannot cancel a {job.Status} job");
            }

            // Active hardware cancellation is asynchronous and durable. Persist the command
            // first; the dedicated consumer transitions the job only after backend acceptance.
            if ((job.Status == PrintJobStatus.Printing || job.Status == PrintJobStatus.Paused || job.Status == PrintJobStatus.Starting)
                && job.AssignedPrinterId.HasValue)
            {
                if (_appDbContext is null)
                {
                    throw new InvalidOperationException(
                        "Durable backend control commands are unavailable.");
                }

                await using QueueOutboxTransactionScope transaction =
                    await QueueOutboxTransactionScope.BeginAsync(_appDbContext, cancellationToken);
                await EnqueueBackendControlCommandAsync(
                    job,
                    userId,
                    operation: "cancel",
                    cancellationToken);
                await _repository.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            await using QueueOutboxTransactionScope? lifecycleTransaction =
                _appDbContext is not null && _outboxSequenceAllocator is not null
                    ? await QueueOutboxTransactionScope.BeginAsync(
                        _appDbContext,
                        cancellationToken)
                    : null;
            PrintJobStatus previousStatus = job.Status;
            DateTime cancelledAt = DateTime.UtcNow;
            job.Status = PrintJobStatus.Cancelled;
            job.UpdatedAt = cancelledAt;
            job.ActualEndTime = cancelledAt;

            if (_appDbContext is not null)
            {
                _appDbContext.JobStateHistories.Add(new JobStateHistory
                {
                    Id = Guid.NewGuid(),
                    JobId = job.Id,
                    FromState = previousStatus.ToString(),
                    ToState = PrintJobStatus.Cancelled.ToString(),
                    TransitionedAtUtc = cancelledAt,
                    CreatedAt = cancelledAt,
                    Notes = "Queue cancellation accepted.",
                });
            }

            await ReleaseDispatchLeaseAsync(job, cancellationToken);
            if (job.AssignedPrinterId.HasValue)
            {
                await AdvanceQueueRevisionAsync(
                    job.AssignedPrinterId.Value,
                    "job cancellation",
                    cancellationToken);
            }

            // Durable audit written in the SAME transaction as the cancellation.
            AddQueueAudit(
                userId,
                QueueAuditOperations.JobCancel,
                QueueAuditOutcomes.Success,
                job);

            // Emit a durable lifecycle outbox event so the publisher broadcasts the cancellation
            // to authorized groups. Written in the SAME transaction as the status change.
            if (_appDbContext is not null && _outboxSequenceAllocator is not null)
            {
                await QueueLifecycleEventWriter.AddEventAsync(
                    _appDbContext,
                    _outboxSequenceAllocator,
                    QueueLifecycleEventWriter.EventTypeJobCancelled,
                    aggregateId: job.Id,
                    printerId: job.AssignedPrinterId,
                    attemptId: null,
                    aggregateRowVersion: job.RowVersion,
                    failureCode: "job_cancelled",
                    payloadJson: QueueLifecycleEventWriter.BuildTerminalPayload(
                        job.Id, job.AssignedPrinterId, null,
                        PrintJobStatus.Cancelled.ToString(),
                        job.JobKind?.ToString() ?? nameof(JobKind.Standard),
                        failureCode: "job_cancelled"),
                    cancellationToken);
            }

            await _repository.SaveChangesAsync(cancellationToken);
            if (lifecycleTransaction is not null)
            {
                await lifecycleTransaction.CommitAsync(cancellationToken);
            }

            if (_coverageBroadcaster is not null && job.AssignedPrinterId.HasValue)
            {
                await _coverageBroadcaster.BroadcastPrinterChangedAsync(
                    job.AssignedPrinterId.Value,
                    Farm.Infrastructure.Services.Spoolman.FilamentCoverageChangeReasons.QueueChanged,
                    cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Print job {JobId} cancelled by user {UserId}", jobId, userId);

            // Send notification
            await SendJobFailureNotificationAsync(job, "Job cancelled by user", cancellationToken);
        }
        catch (Exception ex) when (ex is not QueueRevisionConflictException and
                                         not QueuePreconditionRequiredException and
                                         not QueueSemanticConflictException)
        {
            _logger.LogError(ex, "Error cancelling print job {JobId}", jobId);
            throw;
        }
    }

    /// <summary>
    /// Aborts the current print attempt but keeps the job in the queue.
    /// Sends cancel to printer hardware, resets job status to Queued.
    /// Copies remain unchanged — only the current print attempt is aborted.
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="userId">The unique identifier of the user aborting the print.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AbortPrintAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default) =>
        await AbortPrintAsync(jobId, userId, ifMatchJobRowVersion: null, cancellationToken);

    /// <inheritdoc />
    public async Task AbortPrintAsync(
        string jobId,
        string userId,
        string? ifMatchJobRowVersion,
        CancellationToken cancellationToken = default)
    {
        PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
        if (job is null)
        {
            throw new InvalidOperationException($"Print job {jobId} not found");
        }

        await EnsureActorCanAccessJobAsync(userId, job.Id, cancellationToken);

        QueueRevisionGuard.EnsureIfMatch(ifMatchJobRowVersion, job.RowVersion, "print abort");

        if (job.Status is not (PrintJobStatus.Printing or PrintJobStatus.Paused or PrintJobStatus.Starting))
        {
            throw new QueueSemanticConflictException(
                $"Cannot abort a print that is not currently active (status: {job.Status})");
        }

        if (job.AssignedPrinterId.HasValue)
        {
            if (_appDbContext is null)
            {
                throw new InvalidOperationException(
                    "Durable backend control commands are unavailable.");
            }

            await using QueueOutboxTransactionScope transaction =
                await QueueOutboxTransactionScope.BeginAsync(_appDbContext, cancellationToken);
            await EnqueueBackendControlCommandAsync(
                job,
                userId,
                operation: "abort",
                cancellationToken);
            await _repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using QueueOutboxTransactionScope? lifecycleTransaction =
            _appDbContext is not null && _outboxSequenceAllocator is not null
                ? await QueueOutboxTransactionScope.BeginAsync(
                    _appDbContext,
                    cancellationToken)
                : null;
        job.Status = PrintJobStatus.Queued;
        job.ActualStartTime = null;
        job.UpdatedAt = DateTime.UtcNow;

        await ReleaseDispatchLeaseAsync(job, cancellationToken);
        if (job.AssignedPrinterId.HasValue)
        {
            await AdvanceQueueRevisionAsync(
                job.AssignedPrinterId.Value,
                "print abort",
                cancellationToken);
        }

        AddQueueAudit(
            userId,
            QueueAuditOperations.JobAbort,
            QueueAuditOutcomes.Success,
            job);

        // Emit a durable lifecycle outbox event so the publisher broadcasts the abort
        // (job returned to queued) to authorized groups. Written in the SAME transaction.
        if (_appDbContext is not null && _outboxSequenceAllocator is not null)
        {
            await QueueLifecycleEventWriter.AddEventAsync(
                _appDbContext,
                _outboxSequenceAllocator,
                QueueLifecycleEventWriter.EventTypeJobAborted,
                aggregateId: job.Id,
                printerId: job.AssignedPrinterId,
                attemptId: null,
                aggregateRowVersion: job.RowVersion,
                failureCode: null,
                payloadJson: QueueLifecycleEventWriter.BuildTerminalPayload(
                    job.Id, job.AssignedPrinterId, null,
                    PrintJobStatus.Queued.ToString(), // returned to Queued
                    job.JobKind?.ToString() ?? nameof(JobKind.Standard),
                    failureCode: null),
                cancellationToken);
        }

        await _repository.SaveChangesAsync(cancellationToken);
        if (lifecycleTransaction is not null)
        {
            await lifecycleTransaction.CommitAsync(cancellationToken);
        }

        if (_coverageBroadcaster is not null && job.AssignedPrinterId.HasValue)
        {
            await _coverageBroadcaster.BroadcastPrinterChangedAsync(
                job.AssignedPrinterId.Value,
                Farm.Infrastructure.Services.Spoolman.FilamentCoverageChangeReasons.QueueChanged,
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Print aborted for job {JobId} by user {UserId}, job returned to queue", jobId, userId);
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
        CancellationToken cancellationToken = default) =>
        await BulkCancelJobsAsync(
            jobIds,
            userId,
            jobEtags: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<QueueBulkOperationResultDto> BulkCancelJobsAsync(
        List<string> jobIds,
        string userId,
        IReadOnlyDictionary<string, string>? jobEtags,
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
                    string? etag = null;
                    _ = jobEtags?.TryGetValue(jobId, out etag);
                    await CancelJobAsync(jobId, userId, etag, cancellationToken);
                    result.SuccessfulCount++;
                }
                catch (Exception ex) when (ex is not QueuePreconditionRequiredException and
                                                not QueueRevisionConflictException and
                                                not DbUpdateConcurrencyException)
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
    /// Rerun a completed job (add it back to queue)
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job to rerun.</param>
    /// <param name="userId">The unique identifier of the user requesting the rerun.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueuedPrintJobDto> RerunJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default) =>
        await RerunJobAsync(
            jobId,
            userId,
            ifMatchJobRowVersion: null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<QueuedPrintJobDto> RerunJobAsync(
        string jobId,
        string userId,
        string? ifMatchJobRowVersion,
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

            await EnsureActorCanAccessJobAsync(
                userId,
                originalJob.Id,
                cancellationToken);

            QueueRevisionGuard.EnsureIfMatch(
                ifMatchJobRowVersion,
                originalJob.RowVersion,
                "job rerun");

            GcodeFile? sourceGcode = originalJob.GcodeFileId.HasValue
                ? await _repository.GetGcodeFileAsync(
                    originalJob.GcodeFileId.Value,
                    cancellationToken)
                : null;

            // Reclassify from authoritative artifact lineage. A legacy or tampered row whose
            // JobKind says Standard still cannot clone a promoted calibration artifact.
            // A calibration rerun must go through a new calibration workflow (new idempotency
            // key, new acknowledgement, new provenance) — provenance must not be stripped.
            if (originalJob.JobKind == JobKind.FilamentCalibration ||
                (sourceGcode is not null &&
                 QueueJobClassifier.Classify(sourceGcode).JobKind == JobKind.FilamentCalibration))
            {
                throw new InvalidOperationException(
                    "Calibration jobs cannot be rerun through the standard job queue. " +
                    "Create a new calibration attempt with a new idempotency key and acknowledgement.");
            }

            // Prefer a user-friendly name (original filename) when the linked G-code file still exists.
            string newJobName = originalJob.Name;
            GcodeFile? rerunGcodeFile = null;
            if (originalJob.GcodeFileId.HasValue)
            {
                rerunGcodeFile = sourceGcode;
                if (rerunGcodeFile != null)
                {
                    newJobName = rerunGcodeFile.Name;
                }
            }

            // Calibration provenance is intentionally not copied on rerun.
            // A rerun produces a normal print job; calibration jobs must be recreated by a new calibration workflow.
            var newJob = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = newJobName,
                GcodeFileId = originalJob.GcodeFileId,
                AssignedPrinterId = originalJob.AssignedPrinterId,
                JobKind = JobKind.Standard,
                Status = PrintJobStatus.Queued,
                Priority = originalJob.Priority,
                RequiredNozzleDiameter = originalJob.RequiredNozzleDiameter,
                RequiredMaterialType = originalJob.RequiredMaterialType,
                RequiredCapabilities = originalJob.RequiredCapabilities,
                EstimatedPrintTime = originalJob.EstimatedPrintTime,
                EstimatedFilamentUsage = originalJob.EstimatedFilamentUsage,
                DeadlineAtUtc = originalJob.DeadlineAtUtc,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow
            };

            // Carry per-tool requirements across the rerun. Prefer verbatim copy of the
            // original job's JSON (already normalised); rederive from the G-code file if
            // the source lacks the projection (e.g., pre-#710 jobs).
            Farm.Infrastructure.Services.PrintJobs.PrintJobRequirementsMapper.CopyFrom(newJob, originalJob, rerunGcodeFile);

            // Calculate queue position
            newJob.QueuePosition = await AllocateQueuePositionAsync(
                newJob.AssignedPrinterId,
                cancellationToken);

            if (newJob.AssignedPrinterId.HasValue)
            {
                await AdvanceQueueRevisionAsync(
                    newJob.AssignedPrinterId.Value,
                    "job rerun",
                    cancellationToken);
            }

            await _repository.AddAsync(newJob, cancellationToken);
            if (_coverageBroadcaster is not null && newJob.AssignedPrinterId.HasValue)
            {
                await _coverageBroadcaster.BroadcastPrinterChangedAsync(
                    newJob.AssignedPrinterId.Value,
                    Farm.Infrastructure.Services.Spoolman.FilamentCoverageChangeReasons.JobAssignment,
                    cancellationToken).ConfigureAwait(false);
            }

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
    /// Jobs are identified by (ExternalJobId, SourcePrinterId) composite key and
    /// same-printer/same-start-time duplicate checks.
    /// Existing jobs are updated, new jobs are inserted (AddOrUpdate semantics).
    /// </summary>
    /// <param name="printerIds">Optional list of printer identifiers to seed from. If null, seeds from all printers.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task SeedHistoryFromPrintersAsync(
        List<string>? printerIds = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[HistorySeed] Starting history seeding (fetching all available history)");

        await SyncHistoryFromPrintersInternalAsync(
            options: HistorySeedingOptions,
            printerIds: printerIds,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Sync active external jobs from printer history APIs.
    /// Focuses on non-terminal jobs to quickly discover/update externally-started active work.
    /// </summary>
    public async Task SyncActiveExternalJobsFromPrintersAsync(
        List<string>? printerIds = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[ActiveExternalSync] Starting active external job sync (non-terminal focus)");

        await SyncHistoryFromPrintersInternalAsync(
            options: ActiveExternalSyncOptions,
            printerIds: printerIds,
            cancellationToken: cancellationToken);
    }

    private async Task SyncHistoryFromPrintersInternalAsync(
        HistorySyncOptions options,
        List<string>? printerIds,
        CancellationToken cancellationToken)
    {
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

            _logger.LogInformation("[{LogPrefix}] Processing {PrinterCount} printer(s)", options.LogPrefix, printers.Count);

            foreach (Printer printer in printers)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    (int added, int updated, int skipped) = await SeedHistoryFromSinglePrinterAsync(
                        printer,
                        options,
                        cancellationToken);

                    totalAdded += added;
                    totalUpdated += updated;
                    totalSkipped += skipped;
                    printersProcessed++;

                    _logger.LogInformation(
                        "[{LogPrefix}] Printer {PrinterName} ({PrinterId}): Added={Added}, Updated={Updated}, Skipped={Skipped}",
                        options.LogPrefix, printer.Name, printer.Id, added, updated, skipped);
                }
                catch (DbUpdateException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{LogPrefix}] Failed to sync history from printer {PrinterName} ({PrinterId})",
                        options.LogPrefix, printer.Name, printer.Id);
                }
            }

            _logger.LogInformation(
                "[{LogPrefix}] Completed: Printers={PrintersProcessed}, Added={TotalAdded}, Updated={TotalUpdated}, Skipped={TotalSkipped}",
                options.LogPrefix, printersProcessed, totalAdded, totalUpdated, totalSkipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{LogPrefix}] Error syncing queue history", options.LogPrefix);
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
        HistorySyncOptions options,
        CancellationToken cancellationToken)
    {
        PrinterSyncLockState lockState = AcquirePrinterHistorySyncLock(printer.Id);
        bool lockAcquired = false;

        try
        {
            await lockState.Semaphore.WaitAsync(cancellationToken);
            lockAcquired = true;

            int added = 0;
            int updated = 0;
            int skipped = 0;
            bool saveSucceeded = true;

            bool hasWatermark = options.UseSharedHistoryWatermark;
            bool isInitialSeed = hasWatermark && options.AllowInitialBackfill && (!printer.ServiceState?.LastHistorySeedUtc.HasValue ?? true);
            DateTime? seedSinceUtc = hasWatermark ? printer.ServiceState?.LastHistorySeedUtc : null;
            DateTime latestJobTimestamp = hasWatermark
                ? printer.ServiceState?.LastHistorySeedUtc ?? DateTime.MinValue
                : DateTime.MinValue;

            // Get history from printer via PrintersService.
            // Active sync intentionally does not participate in shared history watermark reads/writes.
            HistoryListResponse history = await _printersService.GetHistoryListAsync(
                printer.Id,
                limit: isInitialSeed ? options.InitialFetchLimit : options.IncrementalFetchLimit,
                start: 0,
                since: seedSinceUtc,
                before: null,
                order: null,
                cancellationToken);

            if (history.Jobs.Length == 0)
            {
                _logger.LogDebug("[{LogPrefix}] No history jobs from printer {PrinterName}", options.LogPrefix, printer.Name);
                return (0, 0, 0);
            }

            _logger.LogDebug(
                "[{LogPrefix}] Retrieved {JobCount} history jobs from printer {PrinterName} (initial={IsInitial}, usesWatermark={UsesWatermark})",
                options.LogPrefix, history.Jobs.Length, printer.Name, isInitialSeed, hasWatermark);

            // Get all existing seeded jobs and actual start times for this printer to check for duplicates.
            // History providers report start times as Unix seconds, so exact UTC-second matching is stable here.
            HashSet<string> existingExternalJobIds = await _repository.GetExternalJobIdsForPrinterAsync(
                printer.Id, cancellationToken);
            HashSet<DateTime> existingActualStartTimes = await _repository.GetActualStartTimesForPrinterAsync(
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

                DateTime startTimeUtc = DateTimeOffset.FromUnixTimeSeconds((long)historyJob.StartTime).UtcDateTime;
                bool hasValidStartTime = startTimeUtc > DateTime.UnixEpoch;
                DateTime? endTimeUtc = historyJob.EndTime.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds((long)historyJob.EndTime.Value).UtcDateTime
                    : null;

                PrintJobStatus? mappedStatus = MapHistoryStatusToPrintJobStatus(historyJob.Status, historyJob.EndTime.HasValue);

                // Unclassifiable record (unknown status with no end time): skip rather than
                // fabricate a phantom queued job.
                if (mappedStatus is null)
                {
                    skipped++;
                    continue;
                }

                if (options.ActiveOnly && IsTerminalStatus(mappedStatus.Value))
                {
                    skipped++;
                    continue;
                }

                // Ingest both terminal and non-terminal jobs so externally-started jobs can be tracked.
                // Duplicate protection still relies on external-id dedupe and history-to-existing-job linking.

                // On incremental seed, skip jobs older than or equal to last seed timestamp.
                // This client-side filtering is needed for OctoPrint (which doesn't support server-side filtering).
                // Moonraker already filters server-side via the 'since' parameter, but this acts as a safety net.
                if (hasWatermark && !isInitialSeed && seedSinceUtc.HasValue && startTimeUtc <= seedSinceUtc.Value)
                {
                    skipped++;
                    continue;
                }

                // Track the latest job timestamp for updating LastHistorySeedUtc
                if (hasWatermark && startTimeUtc > latestJobTimestamp)
                {
                    latestJobTimestamp = startTimeUtc;
                }

                try
                {
                    if (existingExternalJobIds.Contains(historyJob.JobId))
                    {
                        // Job exists. First, try to clean up a previously-created duplicate (print initiated via PrintFarmer)
                        // by linking the PrintFarmer job to the history external ID and removing the seeded duplicate.
                        PrintJob? matchingExisting = await _repository.FindExistingJobForHistoryMatchAsync(
                            printer.Id,
                            historyJob.Filename,
                            startTimeUtc,
                            endTimeUtc,
                            cancellationToken);

                        PrintJob? seededJob = null;
                        if (matchingExisting != null)
                        {
                            seededJob = await _repository.GetByExternalIdAsync(
                                printer.Id, historyJob.JobId, cancellationToken);

                            if (seededJob != null && seededJob.Id != matchingExisting.Id)
                            {
                                matchingExisting.ExternalJobId = historyJob.JobId;
                                matchingExisting.SourcePrinterId = printer.Id;
                                UpdatePrintJobFromHistory(matchingExisting, historyJob);
                                matchingExisting.UpdatedAt = DateTime.UtcNow;
                                if (hasValidStartTime)
                                {
                                    existingActualStartTimes.Add(startTimeUtc);
                                }

                                _repository.Remove(seededJob);
                                updated++;
                                continue;
                            }
                        }

                        // Otherwise, update the seeded job only on initial seed (for completeness).
                        if (isInitialSeed)
                        {
                            seededJob ??= await _repository.GetByExternalIdAsync(
                                printer.Id, historyJob.JobId, cancellationToken);

                            if (seededJob != null)
                            {
                                UpdatePrintJobFromHistory(seededJob, historyJob);
                                seededJob.UpdatedAt = DateTime.UtcNow;
                                if (hasValidStartTime)
                                {
                                    existingActualStartTimes.Add(startTimeUtc);
                                }

                                updated++;
                            }
                        }
                        else if (options.UpdateKnownJobsOnIncremental)
                        {
                            seededJob ??= await _repository.GetByExternalIdAsync(
                                printer.Id, historyJob.JobId, cancellationToken);

                            if (seededJob != null)
                            {
                                UpdatePrintJobFromHistory(seededJob, historyJob);
                                seededJob.UpdatedAt = DateTime.UtcNow;
                                if (hasValidStartTime)
                                {
                                    existingActualStartTimes.Add(startTimeUtc);
                                }

                                updated++;
                            }
                            else
                            {
                                skipped++;
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
                        // Snapshot dedupe can go stale under overlapping runs. Re-check external id before insert.
                        PrintJob? existingByExternalId = await _repository.GetByExternalIdAsync(
                            printer.Id,
                            historyJob.JobId,
                            cancellationToken);

                        if (existingByExternalId != null)
                        {
                            existingExternalJobIds.Add(historyJob.JobId);
                            if (isInitialSeed || options.UpdateKnownJobsOnIncremental)
                            {
                                UpdatePrintJobFromHistory(existingByExternalId, historyJob);
                                existingByExternalId.UpdatedAt = DateTime.UtcNow;
                                if (hasValidStartTime)
                                {
                                    existingActualStartTimes.Add(startTimeUtc);
                                }

                                updated++;
                            }
                            else
                            {
                                skipped++;
                            }

                            continue;
                        }

                        // If this print was initiated through PrintFarmer, it may already exist in our DB without
                        // an ExternalJobId/SourcePrinterId link. In that case, update/link it instead of inserting a duplicate.
                        PrintJob? matchingExisting = await _repository.FindExistingJobForHistoryMatchAsync(
                            printer.Id,
                            historyJob.Filename,
                            startTimeUtc,
                            endTimeUtc,
                            cancellationToken);

                        if (matchingExisting != null)
                        {
                            matchingExisting.ExternalJobId = historyJob.JobId;
                            matchingExisting.SourcePrinterId = printer.Id;
                            UpdatePrintJobFromHistory(matchingExisting, historyJob);
                            matchingExisting.UpdatedAt = DateTime.UtcNow;
                            existingExternalJobIds.Add(historyJob.JobId); // Track for this batch
                            if (hasValidStartTime)
                            {
                                existingActualStartTimes.Add(startTimeUtc);
                            }

                            updated++;
                            continue;
                        }

                        if (hasValidStartTime && existingActualStartTimes.Contains(startTimeUtc))
                        {
                            skipped++;
                            continue;
                        }

                        // New job - create it
                        PrintJob newJob = await CreatePrintJobFromHistoryAsync(historyJob, printer.Id, cancellationToken);

                        // Using sync Add() is intentional - we're batching multiple entities and calling SaveChangesAsync at the end.
                        // AddAsync() is only needed when the entity has value-generated properties requiring DB interaction.
#pragma warning disable CA1849 // Call async methods when in an async method
                        _repository.Add(newJob);
#pragma warning restore CA1849
                        existingExternalJobIds.Add(historyJob.JobId); // Track for this batch
                        if (hasValidStartTime)
                        {
                            existingActualStartTimes.Add(startTimeUtc);
                        }

                        added++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{LogPrefix}] Failed to process history job {JobId}", options.LogPrefix, historyJob.JobId);
                    skipped++;
                }
            }

            // Save all changes for this printer
            if (added > 0 || updated > 0)
            {
                try
                {
                    await _repository.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (IsLikelyDuplicateExternalJobConflict(ex))
                {
                    saveSucceeded = false;
                    _logger.LogWarning(
                        ex,
                        "[{LogPrefix}] Ignoring duplicate history ingest conflict for printer {PrinterName} ({PrinterId}); overlapping ingest attempt detected",
                        options.LogPrefix,
                        printer.Name,
                        printer.Id);

                    // Reset tracked state so a failed insert/update from this printer
                    // cannot poison later SaveChanges calls in the same sync run.
                    _repository.ClearTrackedChanges();
                }
            }

            // Update the printer's last seed timestamp only when this sync mode owns the shared watermark.
            if (saveSucceeded
                && options.PersistHistoryWatermark
                && latestJobTimestamp > (printer.ServiceState?.LastHistorySeedUtc ?? DateTime.MinValue))
            {
                await _repository.UpdatePrinterLastHistorySeedAsync(printer.Id, latestJobTimestamp, cancellationToken);
                _logger.LogDebug(
                    "[{LogPrefix}] Updated LastHistorySeedUtc for printer {PrinterName} to {Timestamp}",
                    options.LogPrefix,
                    printer.Name,
                    latestJobTimestamp);
            }

            return (added, updated, skipped);
        }
        finally
        {
            if (lockAcquired)
            {
                lockState.Semaphore.Release();
            }

            if (lockState.ReleaseReferenceAndMarkUsed() == 0)
            {
                TryCleanupStalePrinterHistorySyncLocks();
            }
        }
    }

    /// <inheritdoc />
    public async Task<DeduplicateHistoryResultDto> DeduplicateSeededHistoryAsync(
        bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        List<HistoryDuplicateCandidate> candidates =
            await _repository.GetHistoryDuplicateCandidatesAsync(cancellationToken);

        DeduplicateHistoryResultDto result = new() { DryRun = dryRun };
        List<Guid> idsToRemove = new();

        IEnumerable<IGrouping<(Guid PrinterId, DateTime Start), HistoryDuplicateCandidate>> groups = candidates
            .Where(c => (c.SourcePrinterId ?? c.AssignedPrinterId) != null)
            .GroupBy(c => (
                PrinterId: (c.SourcePrinterId ?? c.AssignedPrinterId)!.Value,
                Start: TruncateToSecond(c.ActualStartTime)));

        foreach (IGrouping<(Guid PrinterId, DateTime Start), HistoryDuplicateCandidate> group in groups)
        {
            List<HistoryDuplicateCandidate> members = group.ToList();
            if (members.Count < 2)
            {
                continue;
            }

            // Retain the most authoritative row: prefer a native (non-seeded) job, then one with an
            // external id, then the earliest-created; only seeded rows are ever removed.
            HistoryDuplicateCandidate survivor = members
                .OrderBy(c => c.WasSeededFromHistory ? 1 : 0)
                .ThenBy(c => string.IsNullOrWhiteSpace(c.ExternalJobId) ? 1 : 0)
                .ThenBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .First();

            // Guard against stranding a real printer-history identity. An external-print placeholder
            // (IsExternalPrint, WasSeededFromHistory == false) carries a synthetic ExternalJobId plus a
            // SourcePrinterId, so a later harvest cannot relink it (that path only matches rows with a
            // null ExternalJobId and SourcePrinterId). Removing the seeded sibling here would delete the
            // row that holds the real provider job id and leave the print permanently unreconcilable, so
            // we skip the group and leave both rows for the harvest to resolve.
            if (!survivor.WasSeededFromHistory && survivor.IsExternalPrint)
            {
                _logger.LogInformation(
                    "History dedup: skipping group for printer {PrinterId} at {Start:o}; survivor {SurvivorId} is an external-print placeholder and removing seeded siblings would strand a real history id",
                    group.Key.PrinterId,
                    group.Key.Start,
                    survivor.Id);
                continue;
            }

            List<Guid> removable = members
                .Where(c => c.Id != survivor.Id && c.WasSeededFromHistory)
                .Select(c => c.Id)
                .ToList();

            if (removable.Count == 0)
            {
                continue;
            }

            result.DuplicateGroups++;
            idsToRemove.AddRange(removable);
            result.Groups.Add(new DeduplicateHistoryGroupDto
            {
                PrinterId = group.Key.PrinterId,
                StartTimeUtc = group.Key.Start,
                RetainedJobId = survivor.Id,
                RemovedJobIds = removable
            });
        }

        result.JobsRemoved = idsToRemove.Count;

        if (dryRun || idsToRemove.Count == 0)
        {
            _logger.LogInformation(
                "History dedup {Mode}: {Groups} duplicate group(s), {Jobs} seeded duplicate job(s){Suffix}",
                dryRun ? "dry-run" : "cleanup",
                result.DuplicateGroups,
                result.JobsRemoved,
                dryRun ? " would be removed" : " removed");
            return result;
        }

        // Removing the retained row's duplicates is safe against re-seeding: the survivor keeps the
        // same whole-second start time, so the harvest-time start-time guard blocks re-insertion.
        List<PrintJob> jobsToRemove = await _repository.GetByIdsAsync(idsToRemove, cancellationToken);

        // Report the count actually loaded for deletion; a concurrent process may have removed a
        // candidate between the initial scan and this tracked load.
        result.JobsRemoved = jobsToRemove.Count;

        foreach (PrintJob job in jobsToRemove)
        {
            _repository.Remove(job);
        }

        try
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent harvest may update a targeted row (RowVersion conflict) or a residual
            // FK-restricted relationship may block the delete. Surface the affected ids so an
            // operator can act, then rethrow so the caller reports the failure rather than a silent
            // partial success.
            _logger.LogError(
                ex,
                "History dedup cleanup failed while removing {Jobs} seeded duplicate job(s) across {Groups} group(s); affected ids: {JobIds}",
                result.JobsRemoved,
                result.DuplicateGroups,
                string.Join(", ", idsToRemove));
            throw;
        }

        _logger.LogInformation(
            "History dedup cleanup: removed {Jobs} seeded duplicate job(s) across {Groups} group(s)",
            result.JobsRemoved,
            result.DuplicateGroups);

        return result;
    }

    private static DateTime TruncateToSecond(DateTime value)
    {
        return value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));
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

        // Try to find matching G-code file by filename so history-seeded jobs that map to an
        // in-progress external print can still be swap-validated authoritatively.
        Guid? gcodeFileId = await FindGcodeFileIdByFilenameAsync(historyJob.Filename, cancellationToken);

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = Path.GetFileNameWithoutExtension(historyJob.Filename) ?? "Unknown",
            Status = MapHistoryStatusToPrintJobStatus(historyJob.Status, historyJob.EndTime.HasValue) ?? PrintJobStatus.Failed,
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

            // Matching G-code file resolved above (may be null when no library match exists)
            GcodeFileId = gcodeFileId
        };

        // Project per-extruder G-code metadata onto the seeded job through the same shared
        // mapper every other creation path uses. No-op when the file has no per-extruder data.
        if (gcodeFileId.HasValue)
        {
            GcodeFile? gcodeFile = await _repository.GetGcodeFileAsync(gcodeFileId.Value, cancellationToken);
            Farm.Infrastructure.Services.PrintJobs.PrintJobRequirementsMapper.PopulateFromGcode(job, gcodeFile);
        }

        return job;
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
        existingJob.Status = MapHistoryStatusToPrintJobStatus(historyJob.Status, historyJob.EndTime.HasValue) ?? PrintJobStatus.Failed;
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
    /// Maps a printer history status string to a <see cref="PrintJobStatus"/>.
    /// Returns <c>null</c> when the record cannot be classified (unknown status with no end time),
    /// signalling the caller to skip seeding it rather than fabricating a phantom queued job.
    /// </summary>
    /// <param name="historyStatus">The raw status string reported by the printer/backend.</param>
    /// <param name="hasEndTime">Whether the history record has an end time (i.e. the print ended).</param>
    private static PrintJobStatus? MapHistoryStatusToPrintJobStatus(string historyStatus, bool hasEndTime)
    {
        switch (historyStatus?.ToLowerInvariant())
        {
            // Terminal, successful. OctoPrint emits "Completed"; PrusaLink emits "FINISHED";
            // some backends use "success".
            case "completed":
            case "finished":
            case "success":
                return PrintJobStatus.Completed;

            // Terminal, user-cancelled. PrusaLink emits "STOPPED" for a user-aborted print.
            case "cancelled":
            case "stopped":
                return PrintJobStatus.Cancelled;

            // Terminal, unsuccessful. Moonraker uses "error"; OctoPrint emits "Failed". Both are
            // known terminal states and must classify — never fall through to the default branch,
            // otherwise a real failed record with no end time would be silently skipped.
            case "error":
            case "failed":
                return PrintJobStatus.Failed;

            // Klipper-lifecycle interruptions: the print ended without completing
            // (e.g. firmware restart mid/near print). These are terminal, unsuccessful attempts,
            // not queued work. Mapping them to Queued created phantom active-queue entries.
            case "klippy_shutdown":
            case "klippy_disconnect":
            case "server_exit":
            case "interrupted":
                return PrintJobStatus.Failed;

            // Non-terminal states. A history record for one of these normally has no end time.
            // If it *does* have an end time the print has clearly ended, so the live state is stale
            // and the attempt was aborted — classify as Failed rather than seed a phantom active job.
            case "in_progress":
            case "printing":
                return hasEndTime ? PrintJobStatus.Failed : PrintJobStatus.Printing;
            case "paused":
                return hasEndTime ? PrintJobStatus.Failed : PrintJobStatus.Paused;
            case "standby":
            case "ready":
                return hasEndTime ? PrintJobStatus.Failed : PrintJobStatus.Queued;

            default:
                // Unknown status: if the record has ended it is a terminal (failed) attempt;
                // otherwise it cannot be classified, so skip it rather than seed a phantom job.
                return hasEndTime ? PrintJobStatus.Failed : null;
        }
    }

    private static PrinterSyncLockState AcquirePrinterHistorySyncLock(Guid printerId)
    {
        while (true)
        {
            PrinterSyncLockState created = PrinterSyncLockState.CreateWithReference();
            PrinterSyncLockState state = PrinterHistorySyncLocks.GetOrAdd(printerId, created);

            if (ReferenceEquals(state, created))
            {
                return state;
            }

            if (state.TryAddReference())
            {
                return state;
            }

            PrinterHistorySyncLocks.TryRemove(new KeyValuePair<Guid, PrinterSyncLockState>(printerId, state));
        }
    }

    private static void TryCleanupStalePrinterHistorySyncLocks()
    {
        if (Interlocked.Increment(ref _historySyncReleaseCounter) % 64 != 0)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        foreach (KeyValuePair<Guid, PrinterSyncLockState> entry in PrinterHistorySyncLocks)
        {
            PrinterSyncLockState state = entry.Value;
            if (state.ReferenceCount != 0 || !state.IsIdleFor(PrinterHistorySyncLockIdleTtl, now))
            {
                continue;
            }

            if (!state.TryRetireIfUnused())
            {
                continue;
            }

            PrinterHistorySyncLocks.TryRemove(new KeyValuePair<Guid, PrinterSyncLockState>(entry.Key, state));
        }
    }

    private static bool IsLikelyDuplicateExternalJobConflict(DbUpdateException ex)
    {
        if (TryGetInnerIntProperty(ex, "Microsoft.Data.SqlClient.SqlException", "Number", out int sqlServerNumber)
            && (sqlServerNumber == 2601 || sqlServerNumber == 2627))
        {
            return true;
        }

        if (TryGetInnerStringProperty(ex, "Npgsql.PostgresException", "SqlState", out string? sqlState)
            && string.Equals(sqlState, "23505", StringComparison.Ordinal))
        {
            return true;
        }

        if (TryGetInnerIntProperty(ex, "Microsoft.Data.Sqlite.SqliteException", "SqliteErrorCode", out int sqliteErrorCode)
            && sqliteErrorCode == 19)
        {
            return true;
        }

        string fullMessage = ex.ToString();
        if (fullMessage.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
            || fullMessage.Contains("SQLite Error 19", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryGetInnerIntProperty(ex, "MySqlConnector.MySqlException", "Number", out int mySqlNumber)
            && mySqlNumber == 1062)
        {
            return true;
        }

        if (fullMessage.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase)
            && fullMessage.Contains("for key", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetInnerIntProperty(DbUpdateException ex, string exceptionTypeFullName, string propertyName, out int value)
    {
        value = 0;
        Exception? inner = ex.InnerException;
        if (inner == null || !string.Equals(inner.GetType().FullName, exceptionTypeFullName, StringComparison.Ordinal))
        {
            return false;
        }

        object? property = inner.GetType().GetProperty(propertyName)?.GetValue(inner);
        if (property is int intValue)
        {
            value = intValue;
            return true;
        }

        return false;
    }

    private static bool TryGetInnerStringProperty(DbUpdateException ex, string exceptionTypeFullName, string propertyName, out string? value)
    {
        value = null;
        Exception? inner = ex.InnerException;
        if (inner == null || !string.Equals(inner.GetType().FullName, exceptionTypeFullName, StringComparison.Ordinal))
        {
            return false;
        }

        value = inner.GetType().GetProperty(propertyName)?.GetValue(inner)?.ToString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsTerminalStatus(PrintJobStatus status)
    {
        return status is PrintJobStatus.Completed or PrintJobStatus.Failed or PrintJobStatus.Cancelled;
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
    private static DateTime? NormalizeUtcDeadline(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };
    }

    private QueuedPrintJobWithFileMetaDto MapToQueuedPrintJobWithFileMeta(
        PrintJob job,
        Dictionary<Guid, string?> dispatchVersions)
    {
        DateTime? estimatedStart = EstimateStartTime(job);
        DateTime? estimatedCompletion = EstimateCompletionTime(job, estimatedStart);

        return new QueuedPrintJobWithFileMetaDto
        {
            Job = MapToQueuedPrintJobDto(job),
            GcodeFile = job.GcodeFile != null ? MapToQueueGcodeFileMetaDto(job.GcodeFile) : new QueueGcodeFileMetaDto { FileName = string.IsNullOrWhiteSpace(job.Name) ? "Unknown" : job.Name },
            AssignedPrinter = job.AssignedPrinter != null ? MapToQueuePrinterMetaDto(job.AssignedPrinter) : null,
            DispatchStateRowVersion =
                job.AssignedPrinterId.HasValue &&
                dispatchVersions.TryGetValue(
                    job.AssignedPrinterId.Value,
                    out string? dispatchVersion)
                    ? dispatchVersion
                    : null,
            EstimatedStartTime = estimatedStart,
            EstimatedCompletionTime = estimatedCompletion
        };
    }

    private static DateTime? EstimateStartTime(PrintJob job)
    {
        if (job.ActualStartTime.HasValue)
        {
            return job.ActualStartTime.Value;
        }

        return null;
    }

    private static DateTime? EstimateCompletionTime(PrintJob job, DateTime? estimatedStart)
    {
        if (!estimatedStart.HasValue || !job.EstimatedPrintTime.HasValue)
        {
            return null;
        }

        return estimatedStart.Value + job.EstimatedPrintTime.Value;
    }

    private QueuePlanningSettings GetQueuePlanningSettings()
    {
        QueuePlanningSettings fallback = new();
        if (_settingsService is null)
        {
            return fallback;
        }

        try
        {
            QueuePlanningSettings? settings = _settingsService.Get<QueuePlanningSettings>();
            if (settings is null)
            {
                _logger.LogWarning("QueuePlanning settings were missing. Enforcing strict deadline fallback policy.");
                return new QueuePlanningSettings
                {
                    RequireDeadline = true,
                    MinimumLeadHours = 0,
                    DefaultDeadlineHours = null
                };
            }

            settings.Validate();
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load QueuePlanning settings. Enforcing strict deadline fallback policy.");
            return new QueuePlanningSettings
            {
                RequireDeadline = true,
                MinimumLeadHours = 0,
                DefaultDeadlineHours = null
            };
        }
    }

    private static DateTime? ResolveEnqueueDeadline(DateTime? requestedDeadlineAtUtc, QueuePlanningSettings settings)
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime? normalizedDeadline = NormalizeUtcDeadline(requestedDeadlineAtUtc);
        if (!normalizedDeadline.HasValue)
        {
            if (settings.RequireDeadline)
            {
                throw new ValidationException("Deadline is required by queue policy.");
            }

            if (settings.DefaultDeadlineHours.HasValue)
            {
                normalizedDeadline = nowUtc.AddHours(settings.DefaultDeadlineHours.Value);
            }
        }

        ValidateDeadlineLeadTime(normalizedDeadline, settings.MinimumLeadHours, nowUtc);
        return normalizedDeadline;
    }

    private static DateTime ValidateProvidedDeadline(DateTime? requestedDeadlineAtUtc, QueuePlanningSettings settings)
    {
        DateTime? normalized = NormalizeUtcDeadline(requestedDeadlineAtUtc);
        if (!normalized.HasValue)
        {
            throw new ValidationException("Deadline is required by queue policy.");
        }

        ValidateDeadlineLeadTime(normalized, settings.MinimumLeadHours, DateTime.UtcNow);
        return normalized.Value;
    }

    // ============= AUDIT & LEASE HELPERS (issue #900) =============
    private async Task EnsureActorCanAccessJobAsync(
        string actorSubject,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        if (_resourceAuthorization is null)
        {
            return;
        }

        bool allowed = await _resourceAuthorization.CanActorAccessJobAsync(
            actorSubject,
            jobId,
            PrinterGroupAccessLevel.Submit,
            cancellationToken);
        if (!allowed)
        {
            throw new KeyNotFoundException($"Print job {jobId} not found.");
        }
    }

    private async Task EnsureActorCanAccessPrinterAsync(
        string actorSubject,
        Guid printerId,
        CancellationToken cancellationToken)
    {
        if (_resourceAuthorization is null)
        {
            return;
        }

        bool allowed = await _resourceAuthorization.CanActorAccessPrinterAsync(
            actorSubject,
            printerId,
            PrinterGroupAccessLevel.Submit,
            cancellationToken);
        if (!allowed)
        {
            throw new KeyNotFoundException($"Printer {printerId} not found.");
        }
    }

    /// <summary>
    /// Adds a durable queue audit row to the shared change tracker so it commits in the
    /// SAME transaction as the operation being audited.
    /// </summary>
    private async Task<int> AllocateQueuePositionAsync(
        Guid? printerId,
        CancellationToken cancellationToken)
    {
        if (_queuePositionAllocator is not null)
        {
            return await _queuePositionAllocator.AllocateAsync(
                printerId,
                cancellationToken);
        }

        if (_appDbContext?.Database.IsRelational() == true)
        {
            throw new InvalidOperationException(
                "A provider-native queue position allocator is required for relational queue writes.");
        }

        return await _repository.GetMaxQueuePositionAsync(cancellationToken) + 1;
    }

    private void AddQueueAudit(
        string actorSubject,
        string operation,
        string outcome,
        PrintJob job,
        string? reasonCode = null,
        object? detail = null)
    {
        if (_appDbContext is null)
        {
            return;
        }

        _ = QueueAuditWriter.Add(
            _appDbContext,
            actorSubject,
            operation,
            outcome,
            nameof(PrintJob),
            resourceId: job.Id,
            printerId: job.AssignedPrinterId,
            printJobId: job.Id,
            reasonCode: reasonCode,
            jobRowVersion: job.RowVersion,
            detail: detail ?? new { jobKind = job.JobKind?.ToString() ?? nameof(JobKind.Standard), status = job.Status.ToString() });
    }

    /// <summary>
    /// Advances the queue generation and invalidates any outstanding exact-job acknowledgement.
    /// </summary>
    private async Task AdvanceQueueRevisionAsync(
        Guid printerId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_appDbContext is null)
        {
            return;
        }

        PrinterDispatchState? state = await _appDbContext.PrinterDispatchStates
            .FirstOrDefaultAsync(candidate => candidate.PrinterId == printerId, cancellationToken);
        if (state is null)
        {
            return;
        }

        state.QueueRevision++;

        _logger.LogInformation(
            "Advanced queue revision for printer {PrinterId} to {QueueRevision} ({Reason})",
            printerId,
            state.QueueRevision,
            reason);
    }

    private async Task EnqueueBackendControlCommandAsync(
        PrintJob job,
        string actorSubject,
        string operation,
        CancellationToken cancellationToken)
    {
        if (_appDbContext is null ||
            _outboxSequenceAllocator is null ||
            !job.AssignedPrinterId.HasValue)
        {
            throw new InvalidOperationException(
                "Durable backend control commands are unavailable.");
        }

        if (operation is not ("pause" or "resume" or "cancel" or "abort"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unsupported durable backend control operation.");
        }

        PrinterDispatchState? dispatchState = await _appDbContext.PrinterDispatchStates
            .FirstOrDefaultAsync(
                state => state.PrinterId == job.AssignedPrinterId.Value,
                cancellationToken);
        if (dispatchState is null)
        {
            dispatchState = new PrinterDispatchState
            {
                PrinterId = job.AssignedPrinterId.Value,
                Revision = 1,
            };
            _appDbContext.PrinterDispatchStates.Add(dispatchState);
        }

        if (dispatchState.ActiveDispatchAttemptId.HasValue &&
            dispatchState.ActiveJobId != job.Id)
        {
            throw new QueueSemanticConflictException(
                "The active dispatch attempt changed before the control command could be queued.");
        }

        DateTime now = DateTime.UtcNow;
        QueueDispatchAttempt? attempt = null;
        if (dispatchState.ActiveDispatchAttemptId.HasValue)
        {
            attempt = await _appDbContext.QueueDispatchAttempts
                .FirstOrDefaultAsync(
                    candidate =>
                        candidate.Id == dispatchState.ActiveDispatchAttemptId.Value &&
                        candidate.PrintJobId == job.Id,
                    cancellationToken);
            if (attempt is null)
            {
                throw new QueueSemanticConflictException(
                    "The persisted active dispatch ownership is inconsistent.");
            }
        }
        else
        {
            long printerRevision = await _appDbContext.Printers
                .Where(printer => printer.Id == job.AssignedPrinterId.Value)
                .Select(printer => printer.ConfigurationRevision)
                .SingleAsync(cancellationToken);
            int attemptNumber = await _appDbContext.QueueDispatchAttempts
                .CountAsync(candidate => candidate.PrintJobId == job.Id, cancellationToken) + 1;
            Guid attemptId = Guid.NewGuid();
            attempt = new QueueDispatchAttempt
            {
                Id = attemptId,
                PrintJobId = job.Id,
                PrinterId = job.AssignedPrinterId.Value,
                PrinterConfigRevision = printerRevision,
                AttemptNumber = attemptNumber,
                ActorSubject = actorSubject,
                StartPathKind = "LegacyControlOwnership",
                ClaimedAtUtc = now,
                BackendAcceptedAtUtc = job.ActualStartTime ?? now,
                Outcome = DispatchAttemptOutcome.Accepted,
                BackendCommandId = $"legacy-{attemptId:N}",
                BackendCorrelationId = $"legacy-{attemptId:N}",
                BackendFileName = job.Name,
                BackendCallPhase = DispatchBackendCallPhase.PostAccept,
                JobRowVersionAtClaim = job.RowVersion,
                DispatchStateRowVersionAtClaim = dispatchState.RowVersion,
                UpdatedAtUtc = now,
            };
            _appDbContext.QueueDispatchAttempts.Add(attempt);
            dispatchState.ActiveJobId = job.Id;
            dispatchState.ActiveDispatchAttemptId = attempt.Id;
        }

        bool controlAlreadyOutstanding = await _appDbContext.QueueDispatchOutbox
            .AsNoTracking()
            .AnyAsync(
                candidate =>
                    candidate.EventType == BackendControlCommandConsumerService.EventType &&
                    candidate.AttemptId == attempt.Id &&
                    (candidate.Status == QueueOutboxEventStatus.Pending ||
                     candidate.Status == QueueOutboxEventStatus.Processing ||
                     (candidate.Status == QueueOutboxEventStatus.DeadLettered &&
                      candidate.FailureCode == "manual_control_reconciliation_required")),
                cancellationToken);
        if (controlAlreadyOutstanding)
        {
            throw new QueueSemanticConflictException(
                "A lifecycle command for this dispatch attempt is already awaiting reconciliation.");
        }

        bool canQueueBehindStart =
            dispatchState.PhysicalControlCommandId.HasValue &&
            string.Equals(
                dispatchState.PhysicalControlOperation,
                "start",
                StringComparison.Ordinal) &&
            operation is "cancel" or "abort";
        if (dispatchState.PhysicalControlCommandId.HasValue && !canQueueBehindStart)
        {
            throw new QueueSemanticConflictException(
                "Another physical command owns the printer barrier.");
        }

        var command = new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = await _outboxSequenceAllocator.AllocateAsync(
                _appDbContext,
                cancellationToken),
            AggregateType = nameof(PrintJob),
            AggregateId = job.Id,
            AggregateRowVersion = job.RowVersion,
            PrinterId = job.AssignedPrinterId,
            ProjectId = job.CalibrationProjectId ?? job.ProjectId,
            CalibrationAttemptId = job.CalibrationAttemptId,
            JobStatus = job.Status.ToString(),
            JobKind = job.JobKind?.ToString() ?? nameof(JobKind.Standard),
            DispatchStateRowVersion = dispatchState.RowVersion,
            AttemptId = attempt.Id,
            EventType = BackendControlCommandConsumerService.EventType,
            SchemaVersion = QueueEventSchemaVersions.Current,
            PayloadJson = JsonSerializer.Serialize(new
            {
                jobId = job.Id,
                printerId = job.AssignedPrinterId.Value,
                attemptId = attempt.Id,
                backendJobId = attempt.BackendJobId,
                backendFileIdentity = attempt.BackendFileIdentity ?? attempt.BackendFileName,
                operation,
                actorSubject,
            }),
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = now,
        };
        _appDbContext.QueueDispatchOutbox.Add(command);
        dispatchState.QueueRevision++;
        dispatchState.Revision = Math.Max(1, dispatchState.Revision) + 1;
        if (!dispatchState.PhysicalControlCommandId.HasValue)
        {
            dispatchState.PhysicalControlCommandId = command.Id;
            dispatchState.PhysicalControlAttemptId = attempt.Id;
            dispatchState.PhysicalControlOperation = operation;
            dispatchState.PhysicalControlActorSubject = actorSubject;
            dispatchState.PhysicalControlStartedAtUtc = null;
            dispatchState.PhysicalControlRequiresReconciliation = false;
        }

        string auditOperation = operation switch
        {
            "pause" => QueueAuditOperations.JobPause,
            "resume" => QueueAuditOperations.JobResume,
            "abort" => QueueAuditOperations.JobAbort,
            _ => QueueAuditOperations.JobCancel,
        };
        _ = QueueAuditWriter.Add(
            _appDbContext,
            actorSubject,
            auditOperation,
            QueueAuditOutcomes.Success,
            nameof(PrintJob),
            resourceId: job.Id,
            printerId: job.AssignedPrinterId,
            printJobId: job.Id,
            dispatchAttemptId: attempt.Id,
            jobRowVersion: job.RowVersion,
            dispatchStateRowVersion: dispatchState.RowVersion,
            detail: new
            {
                commandId = command.Id,
                commandQueued = true,
                syntheticLegacyOwnership = attempt.StartPathKind == "LegacyControlOwnership",
            });
    }

    /// <summary>
    /// Releases the printer dispatch lease and any bed-clear acknowledgement bound to a job
    /// that is leaving the active set (cancel/abort). Without this, a terminal job would leave
    /// its printer permanently marked busy.
    /// </summary>
    private async Task ReleaseDispatchLeaseAsync(PrintJob job, CancellationToken cancellationToken)
    {
        if (_appDbContext is null || !job.AssignedPrinterId.HasValue)
        {
            return;
        }

        PrinterDispatchState? state = await _appDbContext.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == job.AssignedPrinterId.Value, cancellationToken);

        if (state is null)
        {
            return;
        }

        if (state.ActiveJobId == job.Id)
        {
            state.ActiveJobId = null;
            state.ActiveDispatchAttemptId = null;
        }

        if (state.AcknowledgedJobId == job.Id)
        {
            state.AcknowledgedJobId = null;
            state.AcknowledgedAtUtc = null;
            state.AcknowledgedBySubject = null;
            state.AcknowledgementIdempotencyKey = null;
            state.AcknowledgementExpiresAtUtc = null;
            state.AcknowledgedJobRowVersion = null;
            state.AcknowledgedQueueRevision = null;
            state.AcknowledgedPrinterConfigRevision = null;
        }

        // Mark any in-flight attempt for this job as terminal so the reconciler stops
        // probing an attempt whose job is already cancelled/aborted.
        List<QueueDispatchAttempt> openAttempts = await _appDbContext.QueueDispatchAttempts
            .Where(a => a.PrintJobId == job.Id &&
                        (a.Outcome == DispatchAttemptOutcome.InProgress ||
                         a.Outcome == DispatchAttemptOutcome.Unknown))
            .ToListAsync(cancellationToken);

        foreach (QueueDispatchAttempt attempt in openAttempts)
        {
            attempt.Outcome = DispatchAttemptOutcome.FailedBeforeStart;
            attempt.ErrorCode ??= "job_terminated";
            attempt.ErrorDetail ??= "The job was cancelled or aborted before the attempt completed.";
            attempt.RequiresReconciliation = false;
            attempt.IsRetryable = false;
            attempt.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static void ValidateDeadlineLeadTime(DateTime? deadlineAtUtc, int minimumLeadHours, DateTime nowUtc)
    {
        int effectiveMinimumLeadHours = Math.Max(0, minimumLeadHours);
        if (!deadlineAtUtc.HasValue || effectiveMinimumLeadHours == 0)
        {
            return;
        }

        DateTime minimumAllowedDeadline = nowUtc.AddHours(effectiveMinimumLeadHours);
        if (deadlineAtUtc.Value < minimumAllowedDeadline)
        {
            throw new ValidationException(
                $"Deadline must be at least {effectiveMinimumLeadHours} hour(s) in the future.");
        }
    }

    private static QueuePlanningProjection BuildQueuePlanningProjection(
        List<PrintJob> activeJobs,
        QueuePlanningSettings settings,
        DateTime nowUtc)
    {
        if (activeJobs.Count == 0)
        {
            return new QueuePlanningProjection(null, null);
        }

        int bedClearMinutes = Math.Clamp(settings.BedClearMinutes, 0, 120);
        Dictionary<Guid, DateTime> printerAvailableUtc = new();
        Dictionary<Guid, bool> printerHasScheduledWork = new();

        List<PrintJob> assignedJobs = activeJobs
            .Where(job => job.AssignedPrinterId.HasValue)
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.QueuePosition)
            .ToList();

        foreach (IGrouping<Guid, PrintJob> group in assignedJobs.GroupBy(job => job.AssignedPrinterId!.Value))
        {
            DateTime availability = nowUtc;
            bool hasScheduled = false;

            foreach (PrintJob job in group)
            {
                if (hasScheduled && bedClearMinutes > 0)
                {
                    availability = availability.AddMinutes(bedClearMinutes);
                }

                availability = availability.Add(EstimateRemainingDuration(job, nowUtc));
                hasScheduled = true;
            }

            printerAvailableUtc[group.Key] = availability;
            printerHasScheduledWork[group.Key] = hasScheduled;
        }

        List<PrintJob> unassignedJobs = activeJobs
            .Where(job => !job.AssignedPrinterId.HasValue)
            .OrderByDescending(job => job.Priority)
            .ThenBy(job => job.QueuePosition)
            .ToList();

        if (unassignedJobs.Count > 0 && printerAvailableUtc.Count == 0)
        {
            Guid syntheticPlannerLaneId = Guid.Empty;
            printerAvailableUtc[syntheticPlannerLaneId] = nowUtc;
            printerHasScheduledWork[syntheticPlannerLaneId] = false;
        }

        foreach (PrintJob job in unassignedJobs)
        {
            KeyValuePair<Guid, DateTime> earliestLane = printerAvailableUtc.OrderBy(kvp => kvp.Value).First();
            DateTime availability = earliestLane.Value;
            bool hasScheduled = printerHasScheduledWork[earliestLane.Key];

            if (hasScheduled && bedClearMinutes > 0)
            {
                availability = availability.AddMinutes(bedClearMinutes);
            }

            availability = availability.Add(EstimateRemainingDuration(job, nowUtc));
            printerAvailableUtc[earliestLane.Key] = availability;
            printerHasScheduledWork[earliestLane.Key] = true;
        }

        List<DateTime> completionCandidates = printerAvailableUtc
            .Where(kvp => printerHasScheduledWork.TryGetValue(kvp.Key, out bool hasWork) && hasWork)
            .Select(kvp => kvp.Value)
            .ToList();

        if (completionCandidates.Count == 0)
        {
            return new QueuePlanningProjection(null, null);
        }

        DateTime estimatedQueueCompletionUtc = completionCandidates.Max();
        DateTime staffedCompletionUtc = AdjustToStaffedCompletionUtc(
            nowUtc,
            estimatedQueueCompletionUtc,
            settings.WorkdayStartHourUtc,
            settings.WorkdayEndHourUtc);

        return new QueuePlanningProjection(estimatedQueueCompletionUtc, staffedCompletionUtc);
    }

    private static TimeSpan EstimateRemainingDuration(PrintJob job, DateTime nowUtc)
    {
        if (job.EstimatedPrintTime.HasValue && job.EstimatedPrintTime.Value > TimeSpan.Zero)
        {
            if (job.Status is PrintJobStatus.Printing or PrintJobStatus.Starting or PrintJobStatus.Paused
                && job.ActualStartTime.HasValue)
            {
                TimeSpan elapsed = nowUtc - job.ActualStartTime.Value;
                if (elapsed < TimeSpan.Zero)
                {
                    elapsed = TimeSpan.Zero;
                }

                TimeSpan remaining = job.EstimatedPrintTime.Value - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    return remaining;
                }

                return TimeSpan.FromMinutes(MinimumRemainingPrintMinutes);
            }

            return job.EstimatedPrintTime.Value;
        }

        return TimeSpan.FromMinutes(DefaultEstimatedPrintMinutes);
    }

    private static DateTime AdjustToStaffedCompletionUtc(
        DateTime planningStartUtc,
        DateTime estimatedCompletionUtc,
        int workdayStartHourUtc,
        int workdayEndHourUtc)
    {
        if (estimatedCompletionUtc <= planningStartUtc)
        {
            return planningStartUtc;
        }

        TimeSpan remaining = estimatedCompletionUtc - planningStartUtc;
        DateTime current = planningStartUtc;
        int guard = 0;

        while (remaining > TimeSpan.Zero && guard < 10000)
        {
            guard++;

            if (IsWithinWorkingWindow(current, workdayStartHourUtc, workdayEndHourUtc))
            {
                DateTime windowEnd = GetCurrentWindowEnd(current, workdayStartHourUtc, workdayEndHourUtc);
                TimeSpan available = windowEnd - current;
                TimeSpan consumed = available <= remaining ? available : remaining;
                current = current.Add(consumed);
                remaining -= consumed;
                continue;
            }

            current = GetNextWorkingWindowStart(current, workdayStartHourUtc, workdayEndHourUtc);
        }

        return current;
    }

    private static bool IsWithinWorkingWindow(DateTime timestampUtc, int workdayStartHourUtc, int workdayEndHourUtc)
    {
        if (workdayStartHourUtc == workdayEndHourUtc)
        {
            return true;
        }

        TimeSpan start = TimeSpan.FromHours(workdayStartHourUtc);
        TimeSpan end = TimeSpan.FromHours(workdayEndHourUtc);
        TimeSpan current = timestampUtc.TimeOfDay;

        return workdayStartHourUtc < workdayEndHourUtc
            ? current >= start && current < end
            : current >= start || current < end;
    }

    private static DateTime GetCurrentWindowEnd(DateTime timestampUtc, int workdayStartHourUtc, int workdayEndHourUtc)
    {
        if (workdayStartHourUtc == workdayEndHourUtc)
        {
            return timestampUtc.AddYears(1);
        }

        DateTime dayStart = timestampUtc.Date;
        TimeSpan end = TimeSpan.FromHours(workdayEndHourUtc);

        if (workdayStartHourUtc < workdayEndHourUtc)
        {
            return dayStart.Add(end);
        }

        return timestampUtc.TimeOfDay < end
            ? dayStart.Add(end)
            : dayStart.AddDays(1).Add(end);
    }

    private static DateTime GetNextWorkingWindowStart(DateTime timestampUtc, int workdayStartHourUtc, int workdayEndHourUtc)
    {
        if (workdayStartHourUtc == workdayEndHourUtc)
        {
            return timestampUtc;
        }

        DateTime dayStart = timestampUtc.Date;
        DateTime startToday = dayStart.AddHours(workdayStartHourUtc);

        if (workdayStartHourUtc < workdayEndHourUtc)
        {
            return timestampUtc < startToday ? startToday : startToday.AddDays(1);
        }

        return timestampUtc.TimeOfDay < TimeSpan.FromHours(workdayStartHourUtc)
            ? startToday
            : startToday.AddDays(1);
    }

    private readonly record struct QueuePlanningProjection(
        DateTime? EstimatedQueueCompletionUtc,
        DateTime? StaffedCompletionUtc);

    private QueuedPrintJobDto MapToQueuedPrintJobDto(PrintJob job)
    {
        return new QueuedPrintJobDto
        {
            Id = job.Id.ToString(),
            RowVersion = job.RowVersion is { Length: > 0 }
                ? Convert.ToBase64String(job.RowVersion)
                : null,

            // Name = original filename for display (prefer GcodeFile.Name, fallback to job.Name for history-seeded jobs)
            Name = job.GcodeFile?.Name ?? job.Name,
            GcodeFileId = job.GcodeFileId?.ToString(),

            // FileName = internal GUID-based path (null for history-seeded jobs without GcodeFile)
            FileName = job.GcodeFile?.FileName,
            AssignedPrinterId = job.AssignedPrinterId?.ToString(),
            JobKind = (
                job.JobKind ??
                Farm.Infrastructure.Domain.JobKind.Standard).ToString(),
            CalibrationProjectId = job.CalibrationProjectId,
            PrinterName = job.AssignedPrinter?.Name, // Denormalized printer name for display
            PrinterModel = job.AssignedPrinter?.Model?.Name, // Denormalized printer model for display
            Status = job.Status.ToString(),
            Priority = (PrintJobPriority)job.Priority,
            QueuePosition = job.QueuePosition,
            RequiredNozzleDiameter = job.RequiredNozzleDiameter,
            RequiredMaterialType = job.RequiredMaterialType,
            ToolRequirements = Farm.Infrastructure.Services.PrintJobs.PrintJobRequirementsMapper.ToWireRequirements(job),
            RequiredCapabilities = job.RequiredCapabilities,
            EstimatedPrintTimeSeconds = (int?)job.EstimatedPrintTime?.TotalSeconds,
            EstimatedFilamentUsageGrams = job.EstimatedFilamentUsage,
            ActualStartTimeUtc = job.ActualStartTime,
            ActualEndTimeUtc = job.ActualEndTime,
            ActualPrintTimeSeconds = (int?)job.ActualPrintTime?.TotalSeconds,
            ActualFilamentUsageGrams = job.ActualFilamentUsage,
            FailureReason = job.FailureReason,
            Notes = job.Notes,
            SpoolmanFilamentId = job.SpoolmanFilamentId,
            FilamentName = job.FilamentName,
            FilamentVendor = job.FilamentVendor,
            FilamentColor = job.FilamentColor,
            ProjectId = job.ProjectId,
            ProjectName = job.ProjectName,
            EstimatedCost = job.EstimatedCost,
            ActualCost = job.ActualCost,
            Copies = job.Copies,
            CompletedCopies = job.CompletedCopies,
            RemainingCopies = job.RemainingCopies,
            ProjectFileId = job.ProjectFileId,
            ThumbnailUrl = job.GcodeFile != null ? _fileOperations.BuildGcodeThumbnailUrl(job.GcodeFile.Id) : null,
            CreatedAtUtc = job.CreatedAt,
            UpdatedAtUtc = job.UpdatedAt,
            QueuedAtUtc = job.QueuedAt,
            DeadlineAtUtc = job.DeadlineAtUtc,
            WasSeededFromHistory = job.WasSeededFromHistory,
            ToolheadUsages = job.ToolheadUsages
                .OrderBy(tu => tu.ToolheadIndex)
                .Select(tu => new PrintJobToolheadUsageDto(
                    tu.Id,
                    tu.PrintJobId,
                    tu.ToolheadIndex,
                    tu.SpoolmanSpoolId,
                    tu.FilamentUsageGrams,
                    tu.SlicerEstimateGrams,
                    tu.FilamentName,
                    tu.FilamentColor,
                    tu.MaterialCostUsd))
                .ToList(),
            HarvestedAt = job.HarvestedAt
        };
    }

    private async Task<QueuedPrintJobDto> AttachLatestDispatchResultAsync(
        QueuedPrintJobDto dto,
        PrintJob job,
        CancellationToken ct)
    {
        if (_appDbContext is null)
        {
            return dto;
        }

        QueueDispatchAttempt? attempt = await _appDbContext.QueueDispatchAttempts
            .AsNoTracking()
            .Where(candidate => candidate.PrintJobId == job.Id)
            .OrderByDescending(candidate => candidate.ClaimedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (attempt is not null)
        {
            dto.DispatchResult = await MapDispatchAttemptResultAsync(attempt, job, ct);
        }

        return dto;
    }

    private async Task<QueuedPrintJobDto> BuildDispatchResultAsync(
        PrintJob job,
        QueueDispatchAttempt attempt,
        CancellationToken ct)
    {
        QueuedPrintJobDto dto = MapToQueuedPrintJobDto(job);
        dto.DispatchResult = await MapDispatchAttemptResultAsync(attempt, job, ct);
        return dto;
    }

    private QueuedPrintJobDto BuildSupersededDispatchResult(
        PrintJob job,
        QueueDispatchAttempt attempt)
    {
        QueuedPrintJobDto dto = MapToQueuedPrintJobDto(job);
        dto.DispatchResult = new DispatchAttemptResultDto
        {
            AttemptId = attempt.Id,
            AttemptNumber = attempt.AttemptNumber,
            Outcome = DispatchAttemptOutcome.Rejected,
            ErrorCode = "attempt_superseded",
            ErrorDetail =
                "This dispatch attempt no longer owns the printer. Refresh before retrying.",
            IsRetryable = false,
        };
        return dto;
    }

    private static BackendStartOutcome SupersededBackendStart(Guid attemptId) =>
        BackendStartOutcome.Rejected(
            "attempt_superseded",
            "This dispatch attempt no longer owns the printer.",
            attemptId,
            isRetryable: false);

    private static BackendStartOutcome MapDispatchException(
        DispatchExceptionDisposition disposition,
        Guid attemptId) =>
        disposition switch
        {
            DispatchExceptionDisposition.Accepted =>
                BackendStartOutcome.Accepted(attemptId),
            DispatchExceptionDisposition.ReleasedBeforeStart =>
                BackendStartOutcome.FailedBeforeStart(
                    "dispatch_failed_before_start",
                    DispatchPrinterFailure,
                    attemptId,
                    isRetryable: true),
            DispatchExceptionDisposition.AwaitingReconciliation =>
                BackendStartOutcome.Unknown(
                    "The backend outcome could not be determined; reconciliation is required.",
                    attemptId),
            _ => SupersededBackendStart(attemptId),
        };

    private async Task<DispatchAttemptResultDto> MapDispatchAttemptResultAsync(
        QueueDispatchAttempt attempt,
        PrintJob job,
        CancellationToken ct)
    {
        byte[]? dispatchRevision = null;
        if (_appDbContext is not null)
        {
            dispatchRevision = await _appDbContext.PrinterDispatchStates
                .AsNoTracking()
                .Where(state => state.PrinterId == attempt.PrinterId)
                .Select(state => state.RowVersion)
                .FirstOrDefaultAsync(ct);
        }

        string? dispatchStateRevision = dispatchRevision is { Length: > 0 }
            ? Convert.ToBase64String(dispatchRevision)
            : null;
        return QueueDispatchAttemptResultMapper.Map(
            attempt,
            job,
            dispatchStateRevision);
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
            CreatedAtUtc = file.CreatedAt,
            ThumbnailUrl = _fileOperations.BuildGcodeThumbnailUrl(file.Id)
        };
    }

    private QueuePrinterMetaDto MapToQueuePrinterMetaDto(Printer printer)
    {
        PrinterStatusDto? cachedStatus = _printerStatusCache.GetStatus(printer.Id);

        return new QueuePrinterMetaDto
        {
            Id = printer.Id.ToString(),
            RowVersion = printer.RowVersion is { Length: > 0 }
                ? Convert.ToBase64String(printer.RowVersion)
                : null,
            Name = printer.Name,
            ModelName = printer.Model?.Name ?? "Unknown",
            Status = cachedStatus?.State ?? "Unknown",
            IsOnline = cachedStatus?.IsOnline ?? false
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

            if (job is null)
            {
                return null;
            }

            QueuedPrintJobDto dto = MapToQueuedPrintJobDto(job);
            if (_appDbContext is not null)
            {
                QueueDispatchAttempt? attempt = await _appDbContext
                    .QueueDispatchAttempts
                    .AsNoTracking()
                    .Where(candidate => candidate.PrintJobId == job.Id)
                    .OrderByDescending(candidate => candidate.AttemptNumber)
                    .ThenByDescending(candidate => candidate.ClaimedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);
                if (attempt is not null)
                {
                    dto.DispatchResult = await MapDispatchAttemptResultAsync(
                        attempt,
                        job,
                        cancellationToken);
                }
            }

            return dto;
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
    /// <param name="actorSubject">Authenticated actor subject.</param>
    /// <param name="ifMatchJobRowVersion">Required public job ETag.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<QueuedPrintJobDto?> UpdateJobDetailsAsync(
        string jobId,
        UpdateJobDetailsRequest updates,
        string actorSubject,
        string? ifMatchJobRowVersion,
        CancellationToken cancellationToken = default)
    {
        PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
        if (job is null)
        {
            return null;
        }

        await EnsureActorCanAccessJobAsync(actorSubject, job.Id, cancellationToken);
        QueueRevisionGuard.EnsureIfMatch(
            ifMatchJobRowVersion,
            job.RowVersion,
            "job details update");
        return await UpdateJobDetailsAsync(jobId, updates, cancellationToken);
    }

    /// <inheritdoc />
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

            int priorCopies = job.Copies;
            string? priorRequiredMaterialType = job.RequiredMaterialType;

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
                if (!QueueOrdering.IsDefinedPriority((int)updates.Priority.Value))
                {
                    throw new ValidationException(
                        QueueOrdering.UndefinedPriorityMessage((int)updates.Priority.Value));
                }

                job.Priority = (int)updates.Priority.Value;
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

            // Handle Spoolman filament fields
            if (updates.SpoolmanFilamentId.HasValue)
            {
                if (updates.SpoolmanFilamentId.Value == 0)
                {
                    // Clear filament assignment
                    job.SpoolmanFilamentId = null;
                    job.FilamentName = null;
                    job.FilamentVendor = null;
                    job.FilamentColor = null;
                }
                else
                {
                    job.SpoolmanFilamentId = updates.SpoolmanFilamentId.Value;
                    job.FilamentName = updates.FilamentName;
                    job.FilamentVendor = updates.FilamentVendor;
                    job.FilamentColor = updates.FilamentColor;
                }
            }

            // Tag support deferred — the Projects feature provides better job organization
            // than free-form tags. See .squad/decisions/inbox/ for competitive analysis.
            if (updates.Tags != null)
            {
                _logger.LogDebug("Tags update requested but deferred in favor of Projects for job {JobId}", jobId);
            }

            if (updates.Copies.HasValue)
            {
                if (updates.Copies.Value < 1)
                {
                    throw new ArgumentException("Copies must be at least 1", nameof(updates));
                }

                if (updates.Copies.Value < job.CompletedCopies)
                {
                    throw new ArgumentException(
                        $"Copies ({updates.Copies.Value}) cannot be less than already completed copies ({job.CompletedCopies})",
                        nameof(updates));
                }

                job.Copies = updates.Copies.Value;
            }

            if (updates.DeadlineAtUtc.HasValue)
            {
                job.DeadlineAtUtc = ValidateProvidedDeadline(updates.DeadlineAtUtc, GetQueuePlanningSettings());
            }

            job.UpdatedAt = DateTime.UtcNow;
            await _repository.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Job {JobId} details updated: Name={Name}, Priority={Priority}, Notes={NotesLength}",
                jobId, job.Name, job.Priority, job.Notes?.Length ?? 0);

            if (_coverageBroadcaster is not null
                && (priorCopies != job.Copies
                    || !string.Equals(priorRequiredMaterialType, job.RequiredMaterialType, StringComparison.OrdinalIgnoreCase))
                && job.AssignedPrinterId.HasValue
                && job.Status is PrintJobStatus.Queued
                    or PrintJobStatus.Assigned
                    or PrintJobStatus.Starting
                    or PrintJobStatus.Printing
                    or PrintJobStatus.Paused)
            {
                await _coverageBroadcaster.BroadcastPrinterChangedAsync(
                    job.AssignedPrinterId.Value,
                    Farm.Infrastructure.Services.Spoolman.FilamentCoverageChangeReasons.QueueChanged,
                    cancellationToken).ConfigureAwait(false);
            }

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
    /// <param name="actorSubject">Authenticated actor subject.</param>
    /// <param name="ifMatchJobRowVersion">Required public job ETag.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    public async Task<bool> UpdateJobNotesAsync(
        string jobId,
        string? notes,
        string actorSubject,
        string? ifMatchJobRowVersion,
        CancellationToken cancellationToken = default)
    {
        PrintJob? job = await _repository.GetByIdAsync(Guid.Parse(jobId), cancellationToken);
        if (job is null)
        {
            return false;
        }

        await EnsureActorCanAccessJobAsync(actorSubject, job.Id, cancellationToken);
        QueueRevisionGuard.EnsureIfMatch(
            ifMatchJobRowVersion,
            job.RowVersion,
            "job notes update");
        return await UpdateJobNotesAsync(jobId, notes, cancellationToken);
    }

    /// <inheritdoc />
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
                JobName = job.GcodeFile?.Name ?? job.Name,
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
                JobName = job.GcodeFile?.Name ?? job.Name,
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

    /// <inheritdoc />
    public async Task<JobCostBreakdownDto?> GetJobCostBreakdownAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        PrintJob? job = await _repository.GetByIdWithRelationsAsync(jobId, cancellationToken);

        if (job == null)
        {
            return null;
        }

        return new JobCostBreakdownDto
        {
            JobId = job.Id,
            JobName = job.Name ?? job.GcodeFile?.Name ?? "Unknown",
            MaterialCostUsd = job.MaterialCostUsd,
            EnergyCostUsd = job.EnergyCostUsd,
            MachineTimeCostUsd = job.MachineTimeCostUsd,
            LaborCostUsd = job.LaborCostUsd,
            TotalCostUsd = job.TotalCostUsd,
            CostCalculatedAt = job.CostCalculatedAt,
            PrintDuration = job.ActualPrintTime,
            FilamentUsageGrams = job.ActualFilamentUsage,
            FilamentName = job.FilamentName,
            PrinterName = job.AssignedPrinter?.Name,
        };
    }

    /// <inheritdoc />
    public async Task<JobCostBreakdownDto?> UpdateJobCostAsync(
        Guid jobId,
        decimal? materialCost,
        decimal? energyCost,
        decimal? machineTimeCost,
        decimal? laborCost,
        string actorSubject,
        string? ifMatchJobRowVersion,
        CancellationToken cancellationToken = default)
    {
        PrintJob? job = await _repository.GetByIdAsync(jobId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        await EnsureActorCanAccessJobAsync(actorSubject, job.Id, cancellationToken);
        QueueRevisionGuard.EnsureIfMatch(
            ifMatchJobRowVersion,
            job.RowVersion,
            "job cost update");
        return await UpdateJobCostAsync(
            jobId,
            materialCost,
            energyCost,
            machineTimeCost,
            laborCost,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<JobCostBreakdownDto?> UpdateJobCostAsync(
        Guid jobId,
        decimal? materialCost,
        decimal? energyCost,
        decimal? machineTimeCost,
        decimal? laborCost,
        CancellationToken cancellationToken = default)
    {
        if (_jobCostCalculationService == null)
        {
            _logger.LogWarning("JobCostCalculationService is not available. Cannot update cost for job {JobId}.", jobId);
            return await GetJobCostBreakdownAsync(jobId, cancellationToken);
        }

        bool updated = await _jobCostCalculationService.RecalculateCostsWithOverridesAsync(
            jobId,
            materialCost,
            energyCost,
            machineTimeCost,
            laborCost,
            cancellationToken);

        if (!updated)
        {
            _logger.LogWarning("Failed to update cost for job {JobId}.", jobId);
            return null;
        }

        return await GetJobCostBreakdownAsync(jobId, cancellationToken);
    }

    /// <summary>
    /// Snapshots slicer filament estimates from the gcode file into PrintJobToolheadUsage records.
    /// Called at job dispatch time to capture per-toolhead estimates before the job starts.
    /// </summary>
    private async Task SnapshotSlicerEstimatesAsync(PrintJob job, CancellationToken cancellationToken)
    {
        if (job.GcodeFile?.FilamentPerExtruderWeightG is not { } perExtruderJson)
        {
            _logger.LogDebug(
                "Job {JobId} has no per-extruder filament estimates in gcode file — skipping slicer estimate snapshot",
                job.Id);
            return;
        }

        double[]? perExtruderWeights;
        try
        {
            perExtruderWeights = System.Text.Json.JsonSerializer.Deserialize<double[]>(perExtruderJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to parse FilamentPerExtruderWeightG for job {JobId}: {Json}",
                job.Id,
                perExtruderJson);
            return;
        }

        if (perExtruderWeights is not { Length: > 0 })
        {
            _logger.LogDebug(
                "Job {JobId} has empty per-extruder filament estimates — skipping snapshot",
                job.Id);
            return;
        }

        // Load printer toolheads
        var toolheads = await _repository.GetToolheadsForPrinterAsync(job.AssignedPrinterId!.Value, cancellationToken);

        _logger.LogInformation(
            "Snapshotting slicer estimates for job {JobId}: {ExtruderCount} extruders, {ToolheadCount} toolheads configured",
            job.Id,
            perExtruderWeights.Length,
            toolheads.Count);

        // Create PrintJobToolheadUsage records for each extruder with an estimate
        for (int i = 0; i < perExtruderWeights.Length; i++)
        {
            double estimateGrams = perExtruderWeights[i];
            if (estimateGrams <= 0)
            {
                continue; // Skip extruders with zero or negative estimates
            }

            // perExtruderWeights is 0-based G-code T-index; translate each stored toolhead through
            // the mapper so MMU gates (stored 1-based) bind to the correct extruder estimate
            // instead of being shifted by one gate (issue #711 round-10 Finding 2).
            var toolhead = toolheads.FirstOrDefault(t =>
                ToolheadIndexMapper.ToFilamentSourceGcodeToolIndex(t, toolheads) == i);
            var usage = new PrintJobToolheadUsage
            {
                Id = Guid.NewGuid(),
                PrintJobId = job.Id,
                ToolheadIndex = i,
                SlicerEstimateGrams = estimateGrams,
                SpoolmanSpoolId = toolhead?.CurrentSpoolId,
                FilamentName = toolhead?.CurrentMaterial,
                FilamentColor = toolhead?.CurrentFilamentColor
            };

            await _repository.AddToolheadUsageAsync(usage, cancellationToken);

            _logger.LogDebug(
                "Created slicer estimate snapshot for job {JobId}, toolhead T{ToolheadIndex}: {EstimateGrams}g",
                job.Id,
                i,
                estimateGrams);
        }

        await _repository.SaveChangesAsync(cancellationToken);
    }
}
