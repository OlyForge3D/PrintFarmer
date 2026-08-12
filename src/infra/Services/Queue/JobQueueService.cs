using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Logging;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Infrastructure.Services.PrinterGroups;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
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
    private readonly IPrinterGroupService? _printerGroupService;
    private readonly ISettingsService? _settingsService;
    private readonly IFilamentCoverageBroadcaster? _coverageBroadcaster;
    private readonly IPartOutputSnapshotService? _partOutputSnapshotService;
    private readonly AppDbContext? _db;
    private readonly IDbOutboxSequenceAllocator? _sequenceAllocator;
    private readonly IQueuePositionAllocator? _positionAllocator;
    private readonly IQueueResourceAuthorizationService? _resourceAuthorization;

    /// <summary>
    /// Initializes a new instance of the JobQueueService with required dependencies.
    /// </summary>
    /// <param name="repo">Repository for print job persistence and CRUD operations</param>
    /// <param name="dataService">Specialized data service for queue-specific queries</param>
    /// <param name="logger">Unified logging service for operation tracking and audit trails</param>
    /// <param name="costCalculator">Optional cost calculator for estimating job costs from Spoolman data</param>
    /// <param name="dispatchTrigger">Optional dispatch trigger for notifying the auto-dispatch service</param>
    /// <param name="autoDispatchService">Optional auto-dispatch ready-gate service for triggering bed-clear confirmation on idle printers</param>
    /// <param name="printerGroupService">Optional printer group service for ACL checks on queue submission</param>
    /// <param name="settingsService">Optional app settings service for queue deadline policy enforcement</param>
    /// <param name="coverageBroadcaster">Optional filament coverage invalidation broadcaster.</param>
    /// <param name="partOutputSnapshotService">Optional immutable printed-output snapshot service.</param>
    /// <param name="db">Optional database context used for atomic calibration job and outbox persistence</param>
    /// <param name="sequenceAllocator">Optional outbox sequence allocator for cross-process monotonic ordering; required when <paramref name="db"/> is provided</param>
    /// <param name="positionAllocator">Optional provider-native allocator for unique monotonic queue positions.</param>
    /// <param name="resourceAuthorization">Optional service-boundary resource authorization.</param>
    /// <exception cref="ArgumentNullException">Thrown when any required dependency is null</exception>
    public JobQueueService(
        IQueueRepository repo,
        IQueueDataService dataService,
        ILogger<JobQueueService> logger,
        IPrintCostCalculator? costCalculator = null,
        IAutoDispatchTrigger? dispatchTrigger = null,
        IAutoDispatchService? autoDispatchService = null,
        IPrinterGroupService? printerGroupService = null,
        ISettingsService? settingsService = null,
        IFilamentCoverageBroadcaster? coverageBroadcaster = null,
        IPartOutputSnapshotService? partOutputSnapshotService = null,
        AppDbContext? db = null,
        IDbOutboxSequenceAllocator? sequenceAllocator = null,
        IQueuePositionAllocator? positionAllocator = null,
        IQueueResourceAuthorizationService? resourceAuthorization = null)
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
        _printerGroupService = printerGroupService;
        _settingsService = settingsService;
        _coverageBroadcaster = coverageBroadcaster;
        _partOutputSnapshotService = partOutputSnapshotService;
        _db = db;
        _sequenceAllocator = sequenceAllocator;
        _positionAllocator = positionAllocator;
        _resourceAuthorization = resourceAuthorization;
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
            PrintJob? currentJob = allJobs.FirstOrDefault(j => j.Status.OccupiesPrinter());

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

        var dtos = new List<JobQueuePrintJobDto>(jobs.Count);
        foreach (PrintJob job in jobs)
        {
            JobQueuePrintJobDto dto = MapToJobQueuePrintJobDto(
                job,
                job.GcodeFile?.Name ?? string.Empty,
                job.AssignedPrinter?.Name ?? string.Empty);
            dtos.Add(dto);
        }

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
    /// <param name="userId">Optional user ID for ACL enforcement. Null bypasses the check (trusted/system callers).</param>
    /// <param name="ct">Cancellation token for async operation</param>
    /// <returns>JobQueuePrintJobDto with assigned printer and queue position on success; null if gcode file not found or no suitable printer available</returns>
    /// <remarks>
    /// This method creates a new print job and adds it to the queue. If no specific printer is assigned in the request,
    /// the system automatically selects the best available printer based on nozzle diameter and material type compatibility.
    /// If no compatible printer is available, the operation returns null. The job is assigned a queue position based on
    /// the next available position for its assigned printer. Job status defaults to Queued.
    /// </remarks>
    public async Task<JobQueuePrintJobDto?> AddJobToQueueAsync(QueuePrintJobDto request, Guid? userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        GcodeFile? gcode = await _dataService.GetGcodeFileAsync(request.GcodeFileId, ct);
        if (gcode == null)
        {
            return null;
        }

        // Enforce printer group ACL when a userId is provided
        if (userId.HasValue && gcode.PrinterGroupId.HasValue && _printerGroupService is not null)
        {
            bool canSubmit = await _printerGroupService.CanUserSubmitToGroupAsync(gcode.PrinterGroupId.Value, userId.Value, ct);
            if (!canSubmit)
            {
                throw new QueueGroupAccessDeniedException(gcode.PrinterGroupId.Value, userId.Value);
            }
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
            JobKind = request.JobKind,
            IdempotencyKey = request.IdempotencyKey,
            IdempotencyScope = request.IdempotencyScope,
            CalibrationProjectId = request.CalibrationProjectId,
            CalibrationAttemptId = request.CalibrationAttemptId,
            CalibrationConfigSnapshotId = request.CalibrationConfigSnapshotId,
            CalibrationOrchestrationId = request.CalibrationOrchestrationId,
            SourceArtifactId = request.SourceArtifactId,
            GcodeContentSha256 = request.GcodeContentSha256,
            RequiredFirmwareFamily = request.RequiredFirmwareFamily,
            RequiredGcodeDialect = request.RequiredGcodeDialect,
            RequiredSlicerEngine = request.RequiredSlicerEngine,
            RequiredSlicerDistribution = request.RequiredSlicerDistribution,
            RequiredSlicerVersion = request.RequiredSlicerVersion,
            RequiredSlicerContainerDigest = request.RequiredSlicerContainerDigest,
            SpecificationSha256 = request.SpecificationSha256,
            MachineProfileSha256 = request.MachineProfileSha256,
            ProcessProfileSha256 = request.ProcessProfileSha256,
            FilamentProfileSha256 = request.FilamentProfileSha256,
            PrinterConfigSnapshotSha256 = request.PrinterConfigSnapshotSha256,
            PinnedPrinterConfigRevision = request.PinnedPrinterConfigRevision,
            AssignedPrinterId = request.AssignedPrinterId,
            Priority = request.Priority,
            RequiredNozzleDiameter = request.RequiredNozzleDiameter ?? (decimal?)gcode.RequiredNozzleDiameter,
            RequiredMaterialType = request.RequiredMaterialType ?? gcode.RequiredMaterial,
            RequiredPrinterModel = request.RequiredPrinterModel ?? gcode.PrinterModel?.Name ?? gcode.ExtractedPrinterModelName,
            ProjectId = request.ProjectId,
            ProjectName = request.ProjectName,
            SpoolmanFilamentId = request.SpoolmanFilamentId,
            FilamentName = request.FilamentName,
            FilamentVendor = request.FilamentVendor,
            FilamentColor = request.FilamentColor,
            Copies = request.Copies,
            ProjectFileId = request.ProjectFileId,
            PlateIndex = request.PlateIndex,
            PlateName = request.PlateName,
            DeadlineAtUtc = request.DeadlineAtUtc
        };

        // =====================================================================
        // SERVER-AUTHORITATIVE CLASSIFICATION (issue #900, defect 3)
        // The client never decides whether a job is a calibration job, nor what its
        // provenance is. The server inspects the promoted immutable artifact lineage
        // and overwrites the classification and every provenance/compatibility field.
        // =====================================================================
        QueueJobClassification classification = QueueJobClassifier.Classify(gcode);

        if (classification.JobKind == JobKind.FilamentCalibration &&
            request.JobKind is not null &&
            request.JobKind != JobKind.FilamentCalibration)
        {
            // Explicit attempt to launder a calibration artifact through the standard path.
            throw new CalibrationQueueIncompatibleException(
                QueueJobClassifier.CalibrationMisclassificationMessage(request.GcodeFileId));
        }

        if (classification.JobKind != JobKind.FilamentCalibration &&
            request.JobKind == JobKind.FilamentCalibration)
        {
            throw new CalibrationQueueIncompatibleException(
                $"G-code file {request.GcodeFileId} carries no promoted calibration lineage and cannot be queued " +
                "as a calibration job. Calibration jobs must reference a promoted immutable calibration artifact.");
        }

        effectiveRequest.JobKind = classification.JobKind;
        effectiveRequest.CalibrationProjectId = classification.CalibrationProjectId;
        effectiveRequest.CalibrationAttemptId = classification.CalibrationAttemptId;
        effectiveRequest.CalibrationOrchestrationId = classification.CalibrationOrchestrationId;
        effectiveRequest.SourceArtifactId = classification.SourceArtifactId;
        effectiveRequest.GcodeContentSha256 = classification.GcodeContentSha256 ?? gcode.FileHash;

        if (classification.JobKind == JobKind.FilamentCalibration)
        {
            effectiveRequest.SpecificationSha256 = classification.SpecificationSha256;
            effectiveRequest.MachineProfileSha256 = classification.MachineProfileSha256;
            effectiveRequest.ProcessProfileSha256 = classification.ProcessProfileSha256;
            effectiveRequest.FilamentProfileSha256 = classification.FilamentProfileSha256;
            effectiveRequest.RequiredFirmwareFamily = classification.RequiredFirmwareFamily;
            effectiveRequest.RequiredGcodeDialect = classification.RequiredGcodeDialect;
            effectiveRequest.RequiredSlicerEngine = classification.RequiredSlicerEngine;
            effectiveRequest.RequiredSlicerDistribution = classification.RequiredSlicerDistribution;
            effectiveRequest.RequiredSlicerVersion = classification.RequiredSlicerVersion;
            effectiveRequest.RequiredSlicerContainerDigest = classification.RequiredSlicerContainerDigest;
        }

        // Undefined priorities are rejected on every create path.
        if (!QueueOrdering.IsDefinedPriority((int)request.Priority))
        {
            throw new ValidationException(QueueOrdering.UndefinedPriorityMessage((int)request.Priority));
        }

        bool isCalibrationJob = effectiveRequest.JobKind == JobKind.FilamentCalibration;
        CanonicalCalibrationQueueJob? canonicalCalibration = null;
        if (isCalibrationJob)
        {
            if (effectiveRequest.AssignedPrinterId is null)
            {
                throw new ValidationException("Calibration jobs require an assigned printer.");
            }

            if (string.IsNullOrWhiteSpace(effectiveRequest.IdempotencyKey))
            {
                throw new ValidationException("Calibration jobs require an idempotency key.");
            }

            if (effectiveRequest.Copies != 1)
            {
                throw new ValidationException("Calibration jobs must be queued with exactly one copy.");
            }

            if (_db is null)
            {
                throw new InvalidOperationException("Calibration queue writes require a database context.");
            }

            canonicalCalibration = await new CalibrationQueueCanonicalizer(_db)
                .BuildAsync(request, gcode, classification, userId, ct);
        }

        Guid? assignedPrinterId = effectiveRequest.AssignedPrinterId;
        if (assignedPrinterId is null)
        {
            assignedPrinterId = await FindBestAvailablePrinterAsync(effectiveRequest, userId, ct);

            if (assignedPrinterId is null)
            {
                _logger.LogInformation(
                    "No compatible printer found for job. Model={Model}, Material={Material}, Nozzle={Nozzle}",
                    LogSanitizer.Sanitize(effectiveRequest.RequiredPrinterModel ?? "(any)"),
                    LogSanitizer.Sanitize(effectiveRequest.RequiredMaterialType ?? "(any)"),
                    LogSanitizer.Sanitize(effectiveRequest.RequiredNozzleDiameter?.ToString("F2") ?? "(any)"));
                return null;
            }
        }

        if (userId.HasValue &&
            assignedPrinterId.HasValue &&
            _resourceAuthorization is not null &&
            !await _resourceAuthorization.CanActorAccessPrinterAsync(
                userId.Value.ToString(),
                assignedPrinterId.Value,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            throw new UnauthorizedAccessException("The target printer was not found.");
        }

        QueuePlanningSettings queuePlanningSettings = GetQueuePlanningSettings();
        DateTime? resolvedDeadline = ResolveEnqueueDeadline(request.DeadlineAtUtc, queuePlanningSettings);
        DateTime utcNow = DateTime.UtcNow;
        string idempotencyScope = isCalibrationJob
            ? $"calibration-project:{canonicalCalibration!.CalibrationProjectId:N}"
            : string.Empty;
        if (isCalibrationJob &&
            !string.IsNullOrWhiteSpace(effectiveRequest.IdempotencyScope) &&
            !string.Equals(
                effectiveRequest.IdempotencyScope.Trim(),
                idempotencyScope,
                StringComparison.Ordinal))
        {
            throw new CalibrationQueueIncompatibleException(
                "The idempotency scope must match the authoritative calibration project.");
        }

        string? requestSha256 = isCalibrationJob
            ? canonicalCalibration!.ComputeRequestSha256(idempotencyScope)
            : null;

        if (isCalibrationJob)
        {
            JobQueuePrintJobDto? replay = await TryResolveCalibrationReplayAsync(
                idempotencyScope, effectiveRequest.IdempotencyKey!, requestSha256, gcode.Name, ct);

            if (replay is not null)
            {
                return replay;
            }
        }

        PrintJob job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = gcode.Name,
            GcodeFileId = request.GcodeFileId,
            AssignedPrinterId = assignedPrinterId,
            JobKind = canonicalCalibration?.JobKind ?? JobKind.Standard,
            Status = PrintJobStatus.Queued,
            Priority = (int)request.Priority,
            QueuePosition = await AllocateQueuePositionAsync(
                assignedPrinterId.Value,
                ct),
            RequiredNozzleDiameter = canonicalCalibration?.RequiredNozzleDiameter
                ?? effectiveRequest.RequiredNozzleDiameter,

            // Persist the effective scalar (request value falling back to G-code metadata),
            // matching what dispatch/matching already computes above in effectiveRequest.
            // Fixes the pre-#710 gap where the pre-fallback request value was persisted.
            // Calibration pinning (when present) still takes precedence.
            RequiredMaterialType = canonicalCalibration?.RequiredMaterialType
                ?? Farm.Infrastructure.Services.PrintJobs.PrintJobRequirementsMapper
                    .ResolveEffectiveMaterial(request.RequiredMaterialType, gcode),
            RequiredCapabilities = canonicalCalibration?.RequiredCapabilities
                ?? effectiveRequest.RequiredCapabilities,
            EstimatedPrintTime = gcode.EstimatedPrintTimeMinutes.HasValue ? TimeSpan.FromMinutes(gcode.EstimatedPrintTimeMinutes.Value) : null,
            EstimatedFilamentUsage = canonicalCalibration?.EstimatedFilamentUsage
                ?? gcode.EstimatedFilamentWeightG,
            ProjectId = isCalibrationJob ? null : request.ProjectId,
            ProjectName = isCalibrationJob ? null : request.ProjectName,
            SpoolmanFilamentId = isCalibrationJob ? null : request.SpoolmanFilamentId,
            FilamentName = canonicalCalibration?.FilamentName ?? request.FilamentName,
            FilamentVendor = canonicalCalibration?.FilamentVendor ?? request.FilamentVendor,
            FilamentColor = canonicalCalibration?.FilamentColor ?? request.FilamentColor,
            Copies = canonicalCalibration?.Copies ?? request.Copies,
            ProjectFileId = isCalibrationJob ? null : request.ProjectFileId,
            PlateIndex = isCalibrationJob ? null : request.PlateIndex,
            PlateName = isCalibrationJob ? null : request.PlateName,
            CreatedAt = utcNow,
            UpdatedAt = utcNow,
            QueuedAt = utcNow,
            CreatorSubject = userId?.ToString(),
            IdempotencyScope = isCalibrationJob ? idempotencyScope : null,
            IdempotencyKey = isCalibrationJob ? effectiveRequest.IdempotencyKey : null,
            IdempotencyRequestSha256 = requestSha256,
            CalibrationProjectId = canonicalCalibration?.CalibrationProjectId,
            CalibrationAttemptId = canonicalCalibration?.CalibrationAttemptId,
            CalibrationConfigSnapshotId = canonicalCalibration?.CalibrationConfigSnapshotId,
            CalibrationOrchestrationId = canonicalCalibration?.CalibrationOrchestrationId,
            SourceArtifactId = canonicalCalibration?.SourceArtifactId ?? classification.SourceArtifactId,
            SliceJobId = canonicalCalibration?.SliceJobId ?? classification.SliceJobId,
            GcodeContentSha256 = canonicalCalibration?.GcodeContentSha256
                ?? effectiveRequest.GcodeContentSha256,
            PinnedGcodeFileSizeBytes = canonicalCalibration?.GcodeFileSizeBytes,
            RequiredFirmwareFamily = canonicalCalibration?.RequiredFirmwareFamily,
            RequiredGcodeDialect = canonicalCalibration?.RequiredGcodeDialect,
            RequiredSlicerEngine = canonicalCalibration?.RequiredSlicerEngine,
            RequiredSlicerDistribution = canonicalCalibration?.RequiredSlicerDistribution,
            RequiredSlicerVersion = canonicalCalibration?.RequiredSlicerVersion,
            RequiredSlicerContainerDigest = canonicalCalibration?.RequiredSlicerContainerDigest,
            SpecificationSha256 = canonicalCalibration?.SpecificationSha256,
            MachineProfileSha256 = canonicalCalibration?.MachineProfileSha256,
            ProcessProfileSha256 = canonicalCalibration?.ProcessProfileSha256,
            FilamentProfileSha256 = canonicalCalibration?.FilamentProfileSha256,
            PrinterConfigSnapshotSha256 = canonicalCalibration?.PrinterConfigSnapshotSha256,
            PinnedPrinterConfigRevision = canonicalCalibration?.PinnedPrinterConfigRevision,
            PinnedPrinterModelId = canonicalCalibration?.PinnedPrinterModelId,
            PinnedToolheadId = canonicalCalibration?.PinnedToolheadId,
            PinnedToolheadIndex = canonicalCalibration?.PinnedToolheadIndex,
            PinnedSpoolId = canonicalCalibration?.PinnedSpoolId,
            PinnedFilamentSku = canonicalCalibration?.PinnedFilamentSku,
            PinnedFilamentLotNumber = canonicalCalibration?.PinnedFilamentLotNumber,
            FilamentSnapshotSha256 = canonicalCalibration?.FilamentSnapshotSha256,
            SourceModelSha256 = canonicalCalibration?.SourceModelSha256,
            CalibrationManifestSha256 = canonicalCalibration?.CalibrationManifestSha256,
            PinnedObjectDimensionX = canonicalCalibration?.PinnedObjectDimensionX,
            PinnedObjectDimensionY = canonicalCalibration?.PinnedObjectDimensionY,
            PinnedObjectDimensionZ = canonicalCalibration?.PinnedObjectDimensionZ,
            DeadlineAtUtc = isCalibrationJob ? null : resolvedDeadline
        };

        // Project per-extruder G-code metadata onto the newly built job so that
        // JobQueueService, PrintJobManagementService, and rerun all produce identical
        // per-tool requirements — mandatory for authoritative multi-material swap
        // validation on the guided flow endpoint. No-op when the source lacks
        // per-extruder metadata.
        Farm.Infrastructure.Services.PrintJobs.PrintJobRequirementsMapper.PopulateFromGcode(job, gcode);

        await AdvanceQueueRevisionAsync(
            assignedPrinterId.Value,
            "queue insertion",
            ct);

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

        if (isCalibrationJob)
        {
            QueueDispatchOutbox outboxEvent = new()
            {
                Id = Guid.NewGuid(),
                Sequence = 0, // Allocated in the retry loop below.
                AggregateType = nameof(PrintJob),
                AggregateId = job.Id,
                AggregateRowVersion = job.RowVersion,
                PrinterId = job.AssignedPrinterId,
                ProjectId = job.CalibrationProjectId,
                CalibrationAttemptId = job.CalibrationAttemptId,
                JobStatus = job.Status.ToString(),
                JobKind = job.JobKind?.ToString() ?? nameof(JobKind.Standard),
                PrinterConfigRevision = job.PinnedPrinterConfigRevision,
                EventType = "PrintFarmer.Queue.CalibrationJobQueued.v1",
                SchemaVersion = QueueEventSchemaVersions.Current,
                PayloadJson = BuildCalibrationQueueOutboxPayload(job),
                Status = QueueOutboxEventStatus.Pending,
                CreatedAtUtc = utcNow,
            };

            _db!.PrintJobs.Add(job);
            _db.QueueDispatchOutbox.Add(outboxEvent);

            try
            {
                await using QueueOutboxTransactionScope transaction =
                    await QueueOutboxTransactionScope.BeginAsync(_db, ct);
                if (_sequenceAllocator is not null)
                {
                    outboxEvent.Sequence = await _sequenceAllocator.AllocateAsync(_db, ct);
                }
                else
                {
                    if (_db.Database.IsRelational())
                    {
                        throw new InvalidOperationException(
                            "Calibration queue writes require the durable outbox sequence allocator.");
                    }

                    OutboxSequenceState? seqState =
                        await _db.OutboxSequenceStates.SingleOrDefaultAsync(ct);
                    if (seqState is not null)
                    {
                        seqState.NextSequence++;
                        outboxEvent.Sequence = seqState.NextSequence;
                    }
                }

                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogInformation(
                    ex,
                    "[Queue] Calibration create race lost for Scope={Scope}; rereading winner",
                    LogSanitizer.Sanitize(idempotencyScope));
                DetachPendingCalibrationWrite(job, outboxEvent);
                JobQueuePrintJobDto? winner = await TryResolveCalibrationReplayAsync(
                    idempotencyScope,
                    effectiveRequest.IdempotencyKey!,
                    requestSha256,
                    gcode.Name,
                    ct);
                if (winner is not null)
                {
                    return winner;
                }

                throw;
            }
        }
        else
        {
            if (_db is not null && _sequenceAllocator is not null)
            {
                QueueDispatchOutbox outboxEvent = new()
                {
                    Id = Guid.NewGuid(),
                    AggregateType = nameof(PrintJob),
                    AggregateId = job.Id,
                    AggregateRowVersion = job.RowVersion,
                    PrinterId = job.AssignedPrinterId,
                    ProjectId = job.ProjectId,
                    JobStatus = job.Status.ToString(),
                    JobKind = job.JobKind?.ToString() ?? nameof(JobKind.Standard),
                    EventType = "PrintFarmer.Queue.JobQueued.v1",
                    SchemaVersion = QueueEventSchemaVersions.Current,
                    PayloadJson = BuildCalibrationQueueOutboxPayload(job),
                    Status = QueueOutboxEventStatus.Pending,
                    CreatedAtUtc = utcNow,
                };
                await using QueueOutboxTransactionScope transaction =
                    await QueueOutboxTransactionScope.BeginAsync(_db, ct);
                outboxEvent.Sequence =
                    await _sequenceAllocator.AllocateAsync(_db, ct);
                _db.PrintJobs.Add(job);
                _db.QueueDispatchOutbox.Add(outboxEvent);

                // Preserve Epic #705 harvest provenance: capture the idempotent part-output
                // snapshot and dispatch log within the SAME durable transaction as the
                // job/outbox write. No-op when the snapshot service is unavailable.
                if (assignedPrinterId.HasValue)
                {
                    await PrepareFirstAssignmentAsync(job, assignedPrinterId.Value, userId?.ToString("D"), ct);
                }

                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            else
            {
                // Non-durable (e.g. SQLite local dev) path: capture the part-output snapshot
                // alongside the job insert when the snapshot service is available.
                if (_partOutputSnapshotService is null)
                {
                    await _repo.AddAsync(job, ct);
                }
                else
                {
                    await _repo.AddWithoutSaveAsync(job, ct);
                    if (assignedPrinterId.HasValue)
                    {
                        await PrepareFirstAssignmentAsync(job, assignedPrinterId.Value, userId?.ToString("D"), ct);
                    }

                    await _repo.SaveChangesAsync(ct);
                }
            }
        }

        if (_coverageBroadcaster is not null && assignedPrinterId.HasValue)
        {
            await _coverageBroadcaster.BroadcastPrinterChangedAsync(
                assignedPrinterId.Value,
                FilamentCoverageChangeReasons.JobAssignment,
                ct).ConfigureAwait(false);
        }

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

        JobQueuePrintJobDto dto = MapToJobQueuePrintJobDto(
            job,
            gcode.Name,
            (await _dataService.GetAvailablePrintersAsync(ct)).Find(p => p.Id == assignedPrinterId)?.Name ?? "Unknown");
        await ApplyAuthoritativeDispatchProjectionAsync(dto, job, ct);

        return dto;
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
        if (_db?.Database.IsRelational() != true ||
            _db.Database.CurrentTransaction is not null)
        {
            return await GetJobCoreAsync(id, ct);
        }

        return await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using ProviderSafeSerializableTransactionScope transaction =
                await ProviderSafeSerializableTransaction.BeginAsync(_db, ct);
            JobQueuePrintJobDto? dto = await GetJobCoreAsync(id, ct);
            await transaction.CommitAsync(ct);
            return dto;
        });
    }

    private async Task<JobQueuePrintJobDto?> GetJobCoreAsync(Guid id, CancellationToken ct)
    {
        PrintJob? job = await _dataService.GetPrintJobByIdAsync(id, ct);

        if (job is null)
        {
            return null;
        }

        QueueDispatchAttempt? latestAttempt = null;
        if (_db is not null)
        {
            latestAttempt = await _db.QueueDispatchAttempts
                .AsNoTracking()
                .Where(attempt => attempt.PrintJobId == job.Id)
                .OrderByDescending(attempt => attempt.AttemptNumber)
                .ThenByDescending(attempt => attempt.ClaimedAtUtc)
                .FirstOrDefaultAsync(ct);
        }

        JobQueuePrintJobDto dto = MapToJobQueuePrintJobDto(
            job,
            job.GcodeFile?.Name ?? string.Empty,
            job.AssignedPrinter?.Name ?? "Unknown");
        await ApplyAuthoritativeDispatchProjectionAsync(dto, job, ct);
        if (latestAttempt is not null)
        {
            dto.DispatchResult = QueueDispatchAttemptResultMapper.Map(
                latestAttempt,
                job,
                dto.DispatchStateRowVersion);
        }

        return dto;
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
    public async Task<bool> RemoveJobAsync(Guid id, CancellationToken ct) =>
        await RemoveJobAsync(id, ifMatchJobRowVersion: null, ct);

    /// <inheritdoc />
    public async Task<bool> RemoveJobAsync(Guid id, string? ifMatchJobRowVersion, CancellationToken ct)
        => await RemoveJobCoreAsync(id, ifMatchJobRowVersion, actorSubject: null, ct);

    /// <inheritdoc />
    public async Task<bool> RemoveJobAsync(
        Guid id,
        string? ifMatchJobRowVersion,
        string actorSubject,
        CancellationToken ct) =>
        await RemoveJobCoreAsync(id, ifMatchJobRowVersion, actorSubject, ct);

    private async Task<bool> RemoveJobCoreAsync(
        Guid id,
        string? ifMatchJobRowVersion,
        string? actorSubject,
        CancellationToken ct)
    {
        PrintJob? job = await _dataService.GetPrintJobByIdAsync(id, ct);
        if (job == null)
        {
            return false;
        }

        await EnsureActorCanAccessJobAsync(actorSubject, id, ct);
        EnsureIfMatch(ifMatchJobRowVersion, job.RowVersion, "job deletion");

        if (job.Status != PrintJobStatus.Queued && job.Status != PrintJobStatus.Assigned)
        {
            return false;
        }

        Guid? priorAssignedPrinterId = job.AssignedPrinterId;

        // Invalidate any pending bed-clear acknowledgement for this printer so the ack
        // cannot be consumed for a different job after this one is removed.
        await InvalidateAcknowledgementForJobAsync(job, id, "job removal", ct);
        if (job.AssignedPrinterId.HasValue)
        {
            await AdvanceQueueRevisionAsync(job.AssignedPrinterId.Value, "job removal", ct);
        }

        await _repo.RemoveAsync(job, ct);
        if (_coverageBroadcaster is not null && priorAssignedPrinterId.HasValue)
        {
            await _coverageBroadcaster.BroadcastPrinterChangedAsync(
                priorAssignedPrinterId.Value,
                FilamentCoverageChangeReasons.QueueChanged,
                ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Enforces a caller-supplied <c>If-Match</c> token against a persisted row version.
    /// A missing token is a 428; a stale token is a 412. Passing <see langword="null"/>
    /// is only permitted for trusted internal callers that pass <c>null</c> explicitly.
    /// </summary>
    private static void EnsureIfMatch(string? ifMatch, byte[]? actual, string operationDescription)
    {
        if (ifMatch is null)
        {
            // Trusted internal callers (background services) pass null explicitly.
            return;
        }

        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            throw new QueuePreconditionRequiredException(
                $"If-Match is required for {operationDescription}. Fetch the job to obtain its current ETag.");
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(ifMatch);
        }
        catch (FormatException)
        {
            throw new ValidationException("If-Match must be a base-64 encoded ETag.");
        }

        if (!expected.SequenceEqual(actual ?? []))
        {
            throw new QueueRevisionConflictException(
                "The job has changed since the request was prepared. Re-fetch the job ETag and retry.");
        }
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
    public async Task<JobQueuePrintJobDto?> UpdateJobPriorityAsync(
        Guid id,
        UpdateJobPriorityDto request,
        CancellationToken ct) =>
        await UpdateJobPriorityCoreAsync(id, request, actorSubject: null, ct);

    /// <inheritdoc />
    public async Task<JobQueuePrintJobDto?> UpdateJobPriorityAsync(
        Guid id,
        UpdateJobPriorityDto request,
        string actorSubject,
        CancellationToken ct) =>
        await UpdateJobPriorityCoreAsync(id, request, actorSubject, ct);

    private async Task<JobQueuePrintJobDto?> UpdateJobPriorityCoreAsync(
        Guid id,
        UpdateJobPriorityDto request,
        string? actorSubject,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        PrintJob? job = await _dataService.GetPrintJobByIdAsync(id, ct);
        if (job == null)
        {
            return null;
        }

        await EnsureActorCanAccessJobAsync(actorSubject, id, ct);
        EnsureIfMatch(request.IfMatchJobRowVersion, job.RowVersion, "job priority updates");

        // Reject undefined priority values — every mutation must use a valid semantic priority.
        // PrintJobPriority enum: Low=0, Normal=1, High=2, Urgent=3; any other value is rejected.
        if (!QueueOrdering.IsDefinedPriority((int)request.Priority))
        {
            throw new ValidationException(QueueOrdering.UndefinedPriorityMessage((int)request.Priority));
        }

        // Invalidate any pending bed-clear ack when priority changes — the ack was issued for a
        // specific queue position and must be re-issued after reorder.
        await InvalidateAcknowledgementForJobAsync(job, id, "priority change", ct);
        if (job.AssignedPrinterId.HasValue)
        {
            await AdvanceQueueRevisionAsync(job.AssignedPrinterId.Value, "priority change", ct);
        }

        job.Priority = (int)request.Priority;
        job.UpdatedAt = DateTime.UtcNow;
        await _repo.SaveChangesAsync(ct);

        JobQueuePrintJobDto dto = MapToJobQueuePrintJobDto(
            job,
            job.GcodeFile?.Name ?? string.Empty,
            job.AssignedPrinter?.Name ?? "Unknown");
        await ApplyAuthoritativeDispatchProjectionAsync(dto, job, ct);

        return dto;
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
    public async Task<JobQueuePrintJobDto?> UpdateJobAsync(
        Guid id,
        UpdatePrintJobStatusDto request,
        CancellationToken ct) =>
        await UpdateJobCoreAsync(id, request, actorSubject: null, ct);

    /// <inheritdoc />
    public async Task<JobQueuePrintJobDto?> UpdateJobAsync(
        Guid id,
        UpdatePrintJobStatusDto request,
        string actorSubject,
        CancellationToken ct) =>
        await UpdateJobCoreAsync(id, request, actorSubject, ct);

    private async Task<JobQueuePrintJobDto?> UpdateJobCoreAsync(
        Guid id,
        UpdatePrintJobStatusDto request,
        string? actorSubject,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        PrintJob? job = await _dataService.GetPrintJobByIdAsync(id, ct);
        if (job == null)
        {
            return null;
        }

        Guid? priorAssignedPrinterId = job.AssignedPrinterId;
        PrintJobStatus priorStatus = job.Status;

        await EnsureActorCanAccessJobAsync(actorSubject, id, ct);

        // =====================================================================
        // REVISION PRECONDITION (issue #900, defect 4 and 11).
        // The generic update endpoint mutates safety-relevant state (assignment,
        // priority, status) and therefore requires an If-Match token. A stale token
        // is a 412; the caller must re-fetch and retry.
        // =====================================================================
        if (string.IsNullOrWhiteSpace(request.IfMatchJobRowVersion))
        {
            throw new QueuePreconditionRequiredException(
                "If-Match is required for job updates. Fetch the job to obtain its current ETag.");
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(request.IfMatchJobRowVersion);
        }
        catch (FormatException)
        {
            throw new ValidationException("If-Match must be a base-64 encoded ETag.");
        }

        if (!expected.SequenceEqual(job.RowVersion ?? []))
        {
            throw new QueueRevisionConflictException(
                "The job has changed since the request was prepared. Re-fetch the job ETag and retry.");
        }

        if (job.JobKind == JobKind.FilamentCalibration)
        {
            if (request.AssignedPrinterId.HasValue &&
                request.AssignedPrinterId != job.AssignedPrinterId)
            {
                throw new QueueSemanticConflictException(
                    "A calibration job's assigned printer is immutable.");
            }

            if (request.SpoolmanFilamentId.HasValue)
            {
                throw new QueueSemanticConflictException(
                    "A calibration job's pinned spool and filament identity are immutable.");
            }

            if (request.Status.HasValue &&
                request.Status.Value is not (PrintJobStatus.Starting or PrintJobStatus.Printing))
            {
                throw new QueueSemanticConflictException(
                    "Calibration lifecycle transitions must use the dedicated dispatch/cancel/reconcile paths.");
            }
        }

        // =====================================================================
        // STATUS GUARD (issue #900, defect 4).
        // Starting/Printing are reached ONLY through the shared dispatch claim, which
        // enforces bed-clear acknowledgement, telemetry, filament and compatibility
        // gates. The generic update endpoint must never set them.
        // =====================================================================
        if (request.Status.HasValue)
        {
            PrintJobStatus requested = (PrintJobStatus)(int)request.Status.Value;

            if (!Enum.IsDefined(requested))
            {
                throw new ValidationException($"Status value {request.Status} is not a valid PrintJobStatus.");
            }

            if (requested is PrintJobStatus.Starting or PrintJobStatus.Printing)
            {
                throw new ValidationException(
                    "Status 'Starting' and 'Printing' cannot be set through the generic update endpoint. " +
                    "Use the dispatch or bed-clear acknowledgement endpoints so the shared claim guards apply.");
            }

            if (job.Status.OccupiesPrinter())
            {
                throw new ValidationException(
                    $"Job is currently {job.Status}; use the cancel or abort endpoints instead of a generic update.");
            }

            job.Status = requested;
        }

        if (request.Priority.HasValue)
        {
            if (!QueueOrdering.IsDefinedPriority((int)request.Priority.Value))
            {
                throw new ValidationException(QueueOrdering.UndefinedPriorityMessage((int)request.Priority.Value));
            }

            job.Priority = (int)request.Priority.Value;

            // A priority change reorders the queue and therefore invalidates any
            // bed-clear acknowledgement issued for the previous head-of-queue.
            await InvalidateAcknowledgementForJobAsync(job, id, "priority change", ct);
        }

        Guid? originalPrinterId = job.AssignedPrinterId;
        bool queueShapeChanged = request.Priority.HasValue || request.Status.HasValue;

        if (request.AssignedPrinterId.HasValue)
        {
            List<Printer> printer = await _dataService.GetAvailablePrintersAsync(ct);

            // Validate printer exists
            Printer? found = printer.Find(p => p.Id == request.AssignedPrinterId.Value);
            if (found == null)
            {
                return null; // caller will translate to BadRequest
            }

            if (job.AssignedPrinterId != request.AssignedPrinterId.Value)
            {
                if (actorSubject is not null)
                {
                    await EnsureActorCanAccessPrinterAsync(
                        actorSubject,
                        request.AssignedPrinterId.Value,
                        ct);
                }

                // Reassignment invalidates the acknowledgement on BOTH the old and the
                // new printer: the operator confirmed a specific bed for a specific job.
                await InvalidateAcknowledgementForJobAsync(job, id, "printer reassignment", ct);
                await InvalidateAcknowledgementOnPrinterAsync(request.AssignedPrinterId.Value, id, ct);
                job.QueuePosition = await AllocateQueuePositionAsync(
                    request.AssignedPrinterId.Value,
                    ct);
            }

            job.AssignedPrinterId = request.AssignedPrinterId.Value;
            queueShapeChanged |= originalPrinterId != job.AssignedPrinterId;
            if (priorAssignedPrinterId != request.AssignedPrinterId.Value)
            {
                await PrepareFirstAssignmentAsync(job, request.AssignedPrinterId.Value, userId: null, ct);
            }
        }

        if (request.ActualFilamentUsage.HasValue)
        {
            job.ActualFilamentUsage = request.ActualFilamentUsage.Value;
        }

        if (!string.IsNullOrEmpty(request.FailureReason))
        {
            job.FailureReason = request.FailureReason;
        }

        if (request.DeadlineAtUtc.HasValue)
        {
            job.DeadlineAtUtc = ValidateProvidedDeadline(request.DeadlineAtUtc, GetQueuePlanningSettings());
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

        if (queueShapeChanged)
        {
            foreach (Guid printerId in new[] { originalPrinterId, job.AssignedPrinterId }
                         .Where(value => value.HasValue)
                         .Select(value => value!.Value)
                         .Distinct())
            {
                await AdvanceQueueRevisionAsync(printerId, "job update", ct);
            }
        }

        await _repo.SaveChangesAsync(ct);

        bool assignmentChanged = request.AssignedPrinterId.HasValue
            && priorAssignedPrinterId != job.AssignedPrinterId;
        bool statusChanged = request.Status.HasValue && priorStatus != job.Status;
        if (_coverageBroadcaster is not null && (assignmentChanged || statusChanged))
        {
            string reason = assignmentChanged
                ? FilamentCoverageChangeReasons.JobAssignment
                : FilamentCoverageChangeReasons.QueueChanged;
            foreach (Guid printerId in new[] { priorAssignedPrinterId, job.AssignedPrinterId }
                .OfType<Guid>()
                .Distinct())
            {
                await _coverageBroadcaster.BroadcastPrinterChangedAsync(printerId, reason, ct)
                    .ConfigureAwait(false);
            }
        }

        // Reload printer if assignment changed
        if (request.AssignedPrinterId.HasValue)
        {
            job = await _dataService.GetPrintJobByIdAsync(id, ct);
            if (job is null)
            {
                return null;
            }
        }

        JobQueuePrintJobDto dto = MapToJobQueuePrintJobDto(
            job,
            job.GcodeFile?.Name ?? string.Empty,
            job.AssignedPrinter?.Name ?? string.Empty);
        await ApplyAuthoritativeDispatchProjectionAsync(dto, job, ct);

        return dto;
    }

    private async Task PrepareFirstAssignmentAsync(
        PrintJob job,
        Guid printerId,
        string? userId,
        CancellationToken ct)
    {
        if (_partOutputSnapshotService is null)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        job.DispatchedAt ??= now;
        job.DispatchMode ??= (int)DispatchMode.Manual;
        _ = await _partOutputSnapshotService.CaptureJobSnapshotIfAbsentAsync(job, ct);
        _repo.AddDispatchLog(new DispatchLog
        {
            Id = Guid.NewGuid(),
            PrintJobId = job.Id,
            PrinterId = printerId,
            Action = DispatchAction.Dispatched,
            DispatchMode = DispatchMode.Manual,
            DispatchedAt = new DateTimeOffset(now, TimeSpan.Zero),
            DispatchedByUserId = userId,
            Reason = "Assigned during queue operation.",
            CreatedAtUtc = now,
        });
    }

    private async Task<Guid?> FindBestAvailablePrinterAsync(
        QueuePrintJobDto request,
        Guid? userId,
        CancellationToken ct)
    {
        // Filter by model if specified (same logic as manual printer selection)
        List<Printer> printers = string.IsNullOrWhiteSpace(request.RequiredPrinterModel)
            ? await _dataService.GetAvailablePrintersAsync(ct)
            : await _dataService.GetCompatiblePrintersAsync(request.RequiredPrinterModel, ct);

        foreach (Printer printer in printers)
        {
            if (userId.HasValue &&
                _resourceAuthorization is not null &&
                !await _resourceAuthorization.CanActorAccessPrinterAsync(
                    userId.Value.ToString(),
                    printer.Id,
                    PrinterGroupAccessLevel.Submit,
                    ct))
            {
                continue;
            }

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

    private async Task EnsureActorCanAccessJobAsync(
        string? actorSubject,
        Guid jobId,
        CancellationToken ct)
    {
        if (actorSubject is null || _resourceAuthorization is null)
        {
            return;
        }

        if (!await _resourceAuthorization.CanActorAccessJobAsync(
                actorSubject,
                jobId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            throw new KeyNotFoundException($"Print job {jobId} not found.");
        }
    }

    private async Task EnsureActorCanAccessPrinterAsync(
        string actorSubject,
        Guid printerId,
        CancellationToken ct)
    {
        if (_resourceAuthorization is not null &&
            !await _resourceAuthorization.CanActorAccessPrinterAsync(
                actorSubject,
                printerId,
                PrinterGroupAccessLevel.Submit,
                ct))
        {
            throw new KeyNotFoundException($"Printer {printerId} not found.");
        }
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
                tu.SlicerEstimateGrams,
                tu.FilamentName,
                tu.FilamentColor,
                tu.MaterialCostUsd))
            .ToList();

    private static string? ToBase64RowVersion(byte[]? rowVersion) =>
        rowVersion is { Length: > 0 } ? Convert.ToBase64String(rowVersion) : null;

    /// <summary>
    /// Clears any bed-clear acknowledgement issued for <paramref name="jobId"/> on the job's
    /// currently-assigned printer. Called whenever the queue ordering, assignment or job
    /// lifecycle changes so an acknowledgement can never be consumed for a different bed state.
    /// </summary>
    private async Task InvalidateAcknowledgementForJobAsync(
        PrintJob job,
        Guid jobId,
        string reason,
        CancellationToken ct)
    {
        if (!job.AssignedPrinterId.HasValue || _db is null)
        {
            return;
        }

        await InvalidateAcknowledgementOnPrinterAsync(job.AssignedPrinterId.Value, jobId, ct, reason);
    }

    /// <summary>
    /// Clears any bed-clear acknowledgement on <paramref name="printerId"/> that names
    /// <paramref name="jobId"/>.
    /// </summary>
    private async Task InvalidateAcknowledgementOnPrinterAsync(
        Guid printerId,
        Guid jobId,
        CancellationToken ct,
        string reason = "queue change")
    {
        if (_db is null)
        {
            return;
        }

        PrinterDispatchState? ds = await _db.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId, ct);

        if (ds is null || ds.AcknowledgedJobId != jobId)
        {
            return;
        }

        ds.AcknowledgedJobId = null;
        ds.AcknowledgedAtUtc = null;
        ds.AcknowledgedBySubject = null;
        ds.AcknowledgementIdempotencyKey = null;
        ds.AcknowledgementExpiresAtUtc = null;
        ds.AcknowledgedJobRowVersion = null;
        ds.AcknowledgedQueueRevision = null;
        ds.AcknowledgedPrinterConfigRevision = null;

        _logger.LogInformation(
            "[Queue] Invalidated bed-clear ack for job {JobId} on printer {PrinterId} ({Reason})",
            jobId, printerId, LogSanitizer.Sanitize(reason));
    }

    private async Task AdvanceQueueRevisionAsync(
        Guid printerId,
        string reason,
        CancellationToken ct)
    {
        if (_db is null)
        {
            return;
        }

        PrinterDispatchState? state = await _db.PrinterDispatchStates
            .FirstOrDefaultAsync(candidate => candidate.PrinterId == printerId, ct);
        if (state is null)
        {
            return;
        }

        state.QueueRevision++;
        state.AcknowledgedJobId = null;
        state.AcknowledgedAtUtc = null;
        state.AcknowledgedBySubject = null;
        state.AcknowledgementIdempotencyKey = null;
        state.AcknowledgementExpiresAtUtc = null;
        state.AcknowledgedJobRowVersion = null;
        state.AcknowledgedQueueRevision = null;
        state.AcknowledgedPrinterConfigRevision = null;

        _logger.LogInformation(
            "[Queue] Advanced queue revision for printer {PrinterId} to {QueueRevision} ({Reason})",
            printerId,
            state.QueueRevision,
            LogSanitizer.Sanitize(reason));
    }

    /// <summary>
    /// Re-reads the winning calibration job for a (scope, key) pair and returns either an
    /// idempotent replay DTO or throws <see cref="QueueJobIdempotencyConflictException"/>
    /// when the persisted canonical hash differs from the caller's payload.
    /// Returns <see langword="null"/> when no winner exists.
    /// </summary>
    private async Task<JobQueuePrintJobDto?> TryResolveCalibrationReplayAsync(
        string idempotencyScope,
        string idempotencyKey,
        string? requestSha256,
        string fallbackGcodeName,
        CancellationToken ct)
    {
        if (_db is null)
        {
            return null;
        }

        PrintJob? existingJob = await _db.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .FirstOrDefaultAsync(
                j => j.IdempotencyScope == idempotencyScope &&
                     j.IdempotencyKey == idempotencyKey,
                ct);

        if (existingJob is null)
        {
            return null;
        }

        if (!string.Equals(existingJob.IdempotencyRequestSha256, requestSha256, StringComparison.Ordinal))
        {
            throw new QueueJobIdempotencyConflictException(
                "The provided idempotency key was already used with a different calibration payload.");
        }

        JobQueuePrintJobDto dto = MapToJobQueuePrintJobDto(
            existingJob,
            existingJob.GcodeFile?.Name ?? fallbackGcodeName,
            existingJob.AssignedPrinter?.Name ?? "Unknown",
            isIdempotentReplay: true);
        await ApplyAuthoritativeDispatchProjectionAsync(dto, existingJob, ct);

        return dto;
    }

    /// <summary>
    /// Detaches the losing calibration write so the shared <see cref="AppDbContext"/> can be
    /// reused for the winner re-read without replaying the rejected INSERT.
    /// </summary>
    private void DetachPendingCalibrationWrite(PrintJob job, QueueDispatchOutbox outboxEvent)
    {
        if (_db is null)
        {
            return;
        }

        _db.Entry(job).State = EntityState.Detached;
        _db.Entry(outboxEvent).State = EntityState.Detached;

        OutboxSequenceState? seqState = _db.OutboxSequenceStates.Local.SingleOrDefault();
        if (seqState is not null)
        {
            _db.Entry(seqState).State = EntityState.Detached;
        }
    }

    private async Task<int> AllocateQueuePositionAsync(
        Guid printerId,
        CancellationToken ct)
    {
        if (_positionAllocator is not null)
        {
            return await _positionAllocator.AllocateAsync(printerId, ct);
        }

        if (_db?.Database.IsRelational() == true)
        {
            throw new InvalidOperationException(
                "A provider-native queue position allocator is required for relational queue writes.");
        }

        return await _dataService.GetNextQueuePositionAsync(printerId, ct);
    }

    private static string BuildCalibrationQueueOutboxPayload(PrintJob job)
    {
        return JsonSerializer.Serialize(new
        {
            jobId = job.Id,
            jobKind = job.JobKind?.ToString() ?? JobKind.Standard.ToString(),
            printerId = job.AssignedPrinterId,
            calibrationProjectId = job.CalibrationProjectId,
            calibrationAttemptId = job.CalibrationAttemptId,
            queuedAtUtc = job.QueuedAt
        });
    }

    private JobQueuePrintJobDto MapToJobQueuePrintJobDto(
        PrintJob job,
        string gcodeFileName,
        string assignedPrinterName,
        bool isIdempotentReplay = false)
    {
        return new JobQueuePrintJobDto
        {
            Id = job.Id,
            RowVersion = ToBase64RowVersion(job.RowVersion),
            Revision = job.Revision,
            JobKind = job.JobKind,
            CalibrationProjectId = job.CalibrationProjectId,
            CalibrationAttemptId = job.CalibrationAttemptId,
            CalibrationOrchestrationId = job.CalibrationOrchestrationId,
            PinnedPrinterConfigRevision = job.PinnedPrinterConfigRevision,
            IsIdempotentReplay = isIdempotentReplay,
            GcodeFileId = job.GcodeFileId,
            GcodeFileName = gcodeFileName,
            AssignedPrinterId = job.AssignedPrinterId,
            AssignedPrinterName = assignedPrinterName,
            Status = (PrintJobStatus?)job.Status,
            Priority = (PrintJobPriority)job.Priority,
            QueuePosition = job.QueuePosition,
            RequiredNozzleDiameter = job.RequiredNozzleDiameter,
            RequiredMaterialType = job.RequiredMaterialType,
            ToolRequirements = Farm.Infrastructure.Services.PrintJobs.PrintJobRequirementsMapper.ToWireRequirements(job),
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
            PlateIndex = job.PlateIndex,
            PlateName = job.PlateName,
            DeadlineAtUtc = job.DeadlineAtUtc,
            CreatedAt = job.CreatedAt,
            UpdatedAt = job.UpdatedAt,
            ToolheadUsages = MapToolheadUsages(job),
            HarvestedAt = job.HarvestedAt
        };
    }

    private async Task ApplyAuthoritativeDispatchProjectionAsync(
        JobQueuePrintJobDto dto,
        PrintJob job,
        CancellationToken ct)
    {
        dto.BedClearState = job.JobKind == JobKind.FilamentCalibration
            ? BedClearState.Invalidated
            : null;
        if (_db is null)
        {
            return;
        }

        PrinterDispatchState? assignedDispatchState = null;
        if (job.AssignedPrinterId.HasValue)
        {
            assignedDispatchState = await _db.PrinterDispatchStates
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    state => state.PrinterId == job.AssignedPrinterId.Value,
                    ct);
        }

        dto.DispatchStateRowVersion = ToBase64RowVersion(assignedDispatchState?.RowVersion);
        dto.DispatchStateRevision = assignedDispatchState?.Revision;
        if (job.JobKind != JobKind.FilamentCalibration)
        {
            return;
        }

        BedClearCommandRecord? command = await _db.BedClearCommandRecords
            .AsNoTracking()
            .Where(record => record.JobId == job.Id)
            .OrderByDescending(record => record.CreatedAtUtc)
            .ThenByDescending(record => record.Id)
            .FirstOrDefaultAsync(ct);
        if (command is null)
        {
            dto.BedClearState = assignedDispatchState?.AcknowledgedJobId == job.Id
                ? BedClearState.Invalidated
                : BedClearState.None;
            return;
        }

        dto.BedClearCommandId = command.Id;
        dto.BedClearIdempotencyKeySha256 =
            BedClearCommandCorrelation.HashIdempotencyKey(
                command.IdempotencyKey);
        dto.BedClearExpiresAtUtc = command.ExpiresAtUtc;
        if (command.Status is BedClearCommandStatus.Claimed or
            BedClearCommandStatus.Accepted or
            BedClearCommandStatus.Unknown)
        {
            dto.BedClearState = BedClearState.Consumed;
            return;
        }

        if (command.Status is BedClearCommandStatus.Rejected or
            BedClearCommandStatus.Expired)
        {
            dto.BedClearState = BedClearState.Invalidated;
            return;
        }

        PrinterDispatchState? commandDispatchState = assignedDispatchState;
        if (commandDispatchState?.PrinterId != command.PrinterId)
        {
            commandDispatchState = await _db.PrinterDispatchStates
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    state => state.PrinterId == command.PrinterId,
                    ct);
        }

        if (commandDispatchState is null)
        {
            dto.BedClearState = BedClearState.Invalidated;
            return;
        }

        Guid? currentQueueHeadId = await _db.PrintJobs
            .AsNoTracking()
            .Where(candidate =>
                candidate.AssignedPrinterId == command.PrinterId &&
                (candidate.Status == PrintJobStatus.Queued ||
                 candidate.Status == PrintJobStatus.Assigned))
            .OrderByPriorityDescending()
            .Select(candidate => (Guid?)candidate.Id)
            .FirstOrDefaultAsync(ct);
        long? currentPrinterConfigRevision = await _db.Printers
            .AsNoTracking()
            .Where(printer => printer.Id == command.PrinterId)
            .Select(printer => (long?)printer.ConfigurationRevision)
            .SingleOrDefaultAsync(ct);
        dto.BedClearState = BedClearCommandValidity.IsCurrent(
            command,
            job,
            commandDispatchState,
            currentQueueHeadId,
            currentPrinterConfigRevision,
            DateTime.UtcNow)
                ? BedClearState.Acknowledged
                : BedClearState.Invalidated;
    }

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
            _logger.LogWarning(ex, "Failed to load QueuePlanning settings for deadline policy checks. Enforcing strict deadline fallback policy.");
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
}
