using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Farm.Infrastructure;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Authorization;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Contracts.Libraries;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Slicer.Module.Services.Metrics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Api.Controllers.Slicing;

/// <summary>
/// Canonical API endpoints for slice job lifecycle management (submit, claim, progress, complete).
/// </summary>
/// <remarks>
/// This is the single production slice contract. Every worker mutation must present a matching
/// worker credential, claimed job, engine capability, unexpired lease and current fencing token;
/// anything else is rejected rather than tolerated.
/// </remarks>
[ApiController]
[Route("api/slice")]
[Tags("Slice Jobs")]
public partial class SliceJobController(
    ISliceJobRepository jobRepository,
    ISliceJobEventService eventService,
    ILogger<SliceJobController> logger,
    IArtifactsService artifactsService,
    IRateLimitService rateLimitService,
    SliceJobMetrics metrics,
    IWorkerAuthService workerAuth,
    IWorkerRepository workerRepository,
    ISlicerRegistry slicerRegistry,
    ISlicerFileStorage? fileStorage = null,
    IWorkerCircuitBreakerService? circuitBreaker = null,
    ISlicerResourceAccessAuthorizer? resourceAccess = null,
    IPrinterAccessValidator? printerAccess = null,
    IModelStorageResolver? modelStorage = null,
    ICalibrationProfileResolver? profileResolver = null,
    IOptions<JobDispatchRetrySettings>? retryOptions = null,
    IMachineProfileRepository? machineProfileRepository = null,
    IProcessProfileRepository? processProfileRepository = null,
    IFilamentProfileRepository? filamentProfileRepository = null) : ControllerBase
{
    private readonly ISliceJobRepository _jobRepository = jobRepository;
    private readonly ISliceJobEventService _eventService = eventService;
    private readonly ILogger<SliceJobController> _logger = logger;
    private readonly IArtifactsService _artifactsService = artifactsService;
    private readonly IRateLimitService _rateLimitService = rateLimitService;
    private readonly SliceJobMetrics _metrics = metrics;
    private readonly IWorkerAuthService _workerAuth = workerAuth;
    private readonly IWorkerRepository _workerRepository = workerRepository;
    private readonly ISlicerRegistry _slicerRegistry = slicerRegistry ?? throw new ArgumentNullException(nameof(slicerRegistry));
    private readonly ISlicerFileStorage? _fileStorage = fileStorage;
    private readonly IWorkerCircuitBreakerService? _circuitBreaker = circuitBreaker;
    private readonly ISlicerResourceAccessAuthorizer? _resourceAccess = resourceAccess;
    private readonly IPrinterAccessValidator? _printerAccess = printerAccess;
    private readonly IModelStorageResolver? _modelStorage = modelStorage;
    private readonly ICalibrationProfileResolver? _profileResolver = profileResolver;
    private readonly IMachineProfileRepository? _machineProfileRepository = machineProfileRepository;
    private readonly IProcessProfileRepository? _processProfileRepository = processProfileRepository;
    private readonly IFilamentProfileRepository? _filamentProfileRepository = filamentProfileRepository;
    private readonly int _maxClaimRetries = Math.Max(
        0,
        retryOptions?.Value.MaxAttempts ?? new JobDispatchRetrySettings().MaxAttempts);

    /// <summary>
    /// Submits a new slice job.
    /// </summary>
    /// <param name="request">The submission request.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Slicing.Submit)]
    public async Task<IActionResult> SubmitAsync([FromBody] SubmitSliceJobRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        // Unknown engine values are refused outright; they are never cast into an undefined member
        // and never silently fall back to a default engine.
        if (!SlicerEngineNames.IsDefined(request.SlicerEngine))
        {
            return SlicerApiProblems.InvalidRequest(
                this,
                "invalid_slicer_engine",
                "The slicer engine must be one of the supported canonical engine names.");
        }

        if (request.Priority is < 0 or > 3)
        {
            return SlicerApiProblems.InvalidRequest(
                this,
                "invalid_priority",
                "Priority must be between 0 (Low) and 3 (Critical).");
        }

        // Calibration mode (issue #1938): the method name is validated at the API boundary, before
        // the job ever reaches the queue/worker, so an unknown/unsupported method fails with a
        // clear, actionable error rather than a generic slice failure surfacing later.
        CalibrationMethod? calibrationMethod = null;
        if (request.Calibration is not null)
        {
            if (!CalibrationMethods.TryParse(request.Calibration.Method, out CalibrationMethod parsedMethod))
            {
                string message = $"Unsupported calibration method '{request.Calibration.Method}'. " +
                    $"Supported methods: {string.Join(", ", CalibrationMethods.SupportedWireNames)}.";
                return SlicerApiProblems.InvalidRequest(this, "unsupported_calibration_method", message);
            }

            calibrationMethod = parsedMethod;

            // Calibration mode is deliberately independent of, and mutually exclusive with, the
            // unrelated printer/toolhead calibration-projects saga (issue #1940 epic). A calibration-
            // mode request must never also carry saga identifiers — otherwise a client could smuggle
            // a slice job into that saga's send-to-printer refusal path
            // (SlicePrintBridgeController.IsCalibrationSlice) while still resolving as calibration
            // mode here, or vice versa. Reject the request outright rather than silently dropping one.
            if (request.CalibrationProjectId is not null ||
                request.CalibrationAttemptId is not null ||
                request.CalibrationOrchestrationId is not null)
            {
                const string conflictMessage = "A calibration-mode request (calibration.method) cannot also specify " +
                    "calibrationProjectId, calibrationAttemptId, or calibrationOrchestrationId; " +
                    "those belong to the separate printer/toolhead calibration-projects saga.";
                return SlicerApiProblems.InvalidRequest(this, "calibration_mode_conflicts_with_saga_ids", conflictMessage);
            }
        }

        if (_printerAccess is not null &&
            !await _printerAccess.IsEnabledAsync(request.PrinterId, ct))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        // Rate limiting
        SlicerRateLimitResult rateLimitResult = await _rateLimitService.CheckAsync($"slice-job:{userId}", ct);
        if (!rateLimitResult.IsAllowed)
        {
            return StatusCode(429, new { error = "Rate limit exceeded.", retryAfterSeconds = rateLimitResult.RetryAfterSeconds });
        }

        // Resolve relative model file URLs to absolute so workers can download them
        string modelFileUrl = request.ModelFileUrl;
        if (!string.IsNullOrEmpty(modelFileUrl) && modelFileUrl.StartsWith('/'))
        {
            string scheme = HttpContext.Request.Scheme;
            string host = HttpContext.Request.Host.ToString();
            modelFileUrl = $"{scheme}://{host}{modelFileUrl}";
        }

        // Validate per-model transforms require model URLs
        if (request.ModelFileTransforms is { Count: > 0 }
            && request.ModelFileUrls is not { Count: > 0 })
        {
            return BadRequest("ModelFileTransforms requires ModelFileUrls to be provided.");
        }

        // Validate per-model transforms length matches model URLs
        if (request.ModelFileTransforms is { Count: > 0 }
            && request.ModelFileUrls is { Count: > 0 }
            && request.ModelFileTransforms.Count != request.ModelFileUrls.Count)
        {
            return BadRequest($"ModelFileTransforms length ({request.ModelFileTransforms.Count}) must match ModelFileUrls length ({request.ModelFileUrls.Count}).");
        }

        // Resolve and validate multi-model URLs
        List<string>? resolvedModelUrls = null;
        if (request.ModelFileUrls is { Count: > 0 })
        {
            const int maxModelFiles = 20;
            if (request.ModelFileUrls.Count > maxModelFiles)
            {
                return BadRequest($"Too many model files. Maximum is {maxModelFiles}.");
            }

            string scheme = HttpContext.Request.Scheme;
            string host = HttpContext.Request.Host.ToString();
            resolvedModelUrls = [];
            foreach (string url in request.ModelFileUrls)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return BadRequest("Model file URLs must not contain empty entries.");
                }

                string resolved = url.StartsWith('/') ? $"{scheme}://{host}{url}" : url;

                if (!Uri.TryCreate(resolved, UriKind.Absolute, out Uri? parsedUri)
                    || (parsedUri.Scheme != "http" && parsedUri.Scheme != "https"))
                {
                    return BadRequest($"Invalid model file URL: must be an absolute HTTP(S) URL. Got: '{url}'");
                }

                // Same Desktop-scope gap as the single modelFileUrl/model3DId paths below: a
                // multi-model URL can also reference a stored library model
                // ("/api/3d-models/file/{id}") rather than an external location, and the worker
                // later dereferences it with no per-caller scope check (issue #1770 follow-up).
                if (TryGetStoredModelId(resolved, out _) && IsDesktopTokenMissingModelScope())
                {
                    return SlicerApiProblems.ResourceForbidden(this);
                }

                resolvedModelUrls.Add(resolved);
            }
        }

        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PrinterId = request.PrinterId,
            ModelFileUrl = calibrationMethod is { } cm1 ? $"calibration:{CalibrationMethods.ToWireName(cm1)}" : modelFileUrl,
            ModelFileName = calibrationMethod is { } cm2
                ? CalibrationMethods.DefaultModelFileName(cm2)
                : request.ModelFileName,
            SlicerEngine = (int)request.SlicerEngine,
            SlicerEngineName = request.SlicerEngine.ToString(),
            SlicerProfileJson = EmbedExtruderFilamentNames(request.SlicerProfileJson, request.ExtruderFilamentProfileNames),
            SlicerProfileId = request.SlicerProfileId,

            // Server derives RequiredCapabilitiesJson from the (engine, version) tuple below.
            // Client-supplied values are intentionally ignored so a bad/malicious client cannot
            // force the wrong worker to claim the job (issue #578).
            RequiredCapabilitiesJson = null,
            MachineProfileId = request.MachineProfileId,
            ProcessProfileId = request.ProcessProfileId,
            FilamentProfileId = request.FilamentProfileId,
            CalibrationProjectId = request.CalibrationProjectId,
            IdempotencyScopeId = request.CalibrationProjectId ?? Guid.Empty,
            CalibrationAttemptId = request.CalibrationAttemptId,
            CalibrationOrchestrationId = request.CalibrationOrchestrationId,

            // Calibration mode (issue #1938): deliberately independent of the three fields above —
            // this is an ordinary slice job, not a printer/toolhead calibration saga entry.
            CalibrationMethod = calibrationMethod is { } cm3 ? CalibrationMethods.ToWireName(cm3) : null,
            CalibrationParamsJson = request.Calibration?.Params is { Count: > 0 }
                ? JsonSerializer.Serialize(request.Calibration.Params)
                : null,
            OperationId = request.OperationId,
            CorrelationId = request.CorrelationId,
            Checksum = request.Checksum,
            Priority = request.Priority,
            ModelTransformJson = request.ModelTransformJson,
            ExtruderFilamentProfileNamesJson = request.ExtruderFilamentProfileNames is { Count: > 0 }
                ? JsonSerializer.Serialize(request.ExtruderFilamentProfileNames)
                : null,
            ModelFileUrlsJson = resolvedModelUrls is { Count: > 0 }
                ? JsonSerializer.Serialize(resolvedModelUrls)
                : null,
            ModelFileTransformsJson = request.ModelFileTransforms is { Count: > 0 }
                ? JsonSerializer.Serialize(request.ModelFileTransforms)
                : null,
            Status = SliceJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Resolve engine version pin. When null/empty the job is unpinned (legacy
        // behaviour) — any worker for the engine claims it via the generic
        // capability tag. When set, validate against the plugin registry so we
        // never accept a version no worker can serve.
        string engineName = ResolveEngineName((int)request.SlicerEngine);
        string? requestedVersion = string.IsNullOrWhiteSpace(request.SlicerEngineVersion)
            ? null
            : request.SlicerEngineVersion.Trim();

        if (requestedVersion is not null)
        {
            ISlicerLibrary? matched = _slicerRegistry.GetLibrary(engineName, requestedVersion);
            if (matched is null)
            {
                IEnumerable<string> registered = _slicerRegistry.GetLibraries(engineName).Select(l => l.SlicerVersion);
                return BadRequest($"Slicer engine version '{requestedVersion}' is not registered for {engineName}. Registered versions: [{string.Join(", ", registered)}].");
            }

            job.SlicerEngineVersion = requestedVersion;
            job.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { $"{engineName.ToLowerInvariant()}:{requestedVersion}" });
        }
        else
        {
            job.SlicerEngineVersion = null;
            job.RequiredCapabilitiesJson = JsonSerializer.Serialize(new[] { engineName.ToLowerInvariant() });
        }

        // Calibration jobs resolve their model from the worker's own bundled OrcaSlicer resources
        // (issue #1938), so they carry no client-supplied model reference to bind.
        IActionResult? modelFailure = calibrationMethod is null
            ? await BindStoredModelAsync(job, request, userId, ct)
            : null;
        if (modelFailure is not null)
        {
            return modelFailure;
        }

        if (string.IsNullOrWhiteSpace(job.ModelFileName))
        {
            return SlicerApiProblems.InvalidRequest(
                this,
                "model_file_name_required",
                "A model file name is required.");
        }

        IActionResult? profileFailure = await BindResolvedProfilesAsync(job, request, userId, ct);
        if (profileFailure is not null)
        {
            return profileFailure;
        }

        // Named profile submissions (the only path the "New Slice Job" UI uses today) otherwise
        // rely on the worker resolving names against its own bundled OrcaSlicer resources, which
        // has no knowledge of database-stored custom/user profiles and fails immediately for any
        // non-stock filament/machine/process name (issue #1768). Snapshot native JSON here when
        // possible so the job is routed onto the same robust path as ID-based submissions.
        await ResolveNamedProfilesAsync(job, userId, ct);

        try
        {
            await _jobRepository.AddAsync(job, ct);
        }
        catch (DbUpdateException exception)
        {
            // The owner/project-scoped unique indexes are the durable idempotency guard.
            _logger.LogInformation(
                exception,
                "Rejected duplicate slice submission for user {UserId}",
                userId);
            return Conflict(new { error = "A slice job with the same correlation or checksum already exists.", code = "slice_job_duplicate" });
        }

        await _eventService.NotifyJobQueuedAsync(job, ct);

        return Created($"/api/slice/{job.Id}", new SubmitSliceJobResponse
        {
            JobId = job.Id,
            Status = job.Status,
            QueuedAt = job.QueuedAt,
        });
    }

    private static string ResolveEngineName(int engine)
    {
        // Mirrors SlicerEngineType — keep in sync when adding engines.
        return engine switch
        {
            0 => "OrcaSlicer",
            1 => "PrusaSlicer",
            _ => "OrcaSlicer",
        };
    }

    /// <summary>
    /// Gets a slice job by ID.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id}")]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    public async Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
    {
        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is null)
        {
            return SlicerApiProblems.ResourceNotFound(this);
        }

        if (!CanAccess(job))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        return Ok(MapToPublicStatusResponse(job));
    }

    /// <summary>
    /// Gets the current user's slice jobs.
    /// </summary>
    /// <param name="limit">Maximum number of jobs to return (default 100).</param>
    /// <param name="offset">Number of jobs to skip (default 0).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("my-jobs")]
    [Authorize]
    public async Task<IActionResult> GetMyJobsAsync(
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        string userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        if (!Guid.TryParse(userId, out Guid userGuid))
        {
            return BadRequest("Invalid user ID.");
        }

        IReadOnlyList<SliceJob> jobs = await _jobRepository.GetByUserIdAsync(userGuid, limit, offset, ct);
        return Ok(jobs.Select(MapToPublicStatusResponse).ToList());
    }

    /// <summary>
    /// Lists slice jobs. Farm admins see all jobs; other users see only their own.
    /// </summary>
    /// <param name="status">Optional status filter.</param>
    /// <param name="limit">Maximum number of jobs to return (default 50, max 200).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    public async Task<IActionResult> ListAsync([FromQuery] string? status, [FromQuery] int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        IReadOnlyList<SliceJob> jobs;
        if (PrintFarmerPermissions.IsFarmAdmin(User))
        {
            LogAdminBypass("slice-job-list", Guid.Empty);
            jobs = !string.IsNullOrEmpty(status)
                ? await _jobRepository.GetByStatusAsync(status, limit, ct)
                : await _jobRepository.GetQueuedJobsAsync(limit, ct);
        }
        else
        {
            if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
            {
                return SlicerApiProblems.ResourceForbidden(this);
            }

            jobs = await _jobRepository.GetByUserIdAsync(userId, limit, 0, ct);
            if (!string.IsNullOrEmpty(status))
            {
                jobs = jobs
                    .Where(job => string.Equals(job.Status, status, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        return Ok(jobs.Select(MapToPublicStatusResponse).ToList());
    }

    /// <summary>
    /// Worker claims the next available job.
    /// </summary>
    /// <param name="request">Claim request with worker details.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("claim")]
    [WorkerApiKeySecurity] // Public to JWT auth because workers use their registry key and service identity.
    public async Task<IActionResult> ClaimAsync([FromBody] ClaimJobRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        Worker? worker = await GetAuthorizedWorkerAsync();
        if (worker is null ||
            !string.Equals(worker.ServiceId, request.WorkerId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return SlicerApiProblems.AuthenticationRequired(this);
        }

        // Check circuit breaker
        if (_circuitBreaker is not null)
        {
            WorkerCircuitState state = _circuitBreaker.GetCircuitState(worker.Id);
            if (state == WorkerCircuitState.Open)
            {
                return StatusCode(503, new { error = "Circuit breaker is open for this worker.", state = state.ToString() });
            }
        }

        WorkerClaimIdentity claimIdentity = WorkerClaimIdentity.FromRegisteredWorker(worker);
        if (claimIdentity.Capabilities.Length == 0)
        {
            return NoContent();
        }

        SliceJob? job = await _jobRepository.ClaimNextJobAsync(
            claimIdentity,
            request.LeaseDurationSeconds,
            _maxClaimRetries,
            ct);
        if (job is null)
        {
            return NoContent();
        }

        await _eventService.NotifyJobStartedAsync(job, ct);
        _logger.LogInformation("Job {JobId} claimed by worker {WorkerId}", job.Id, worker.Id);

        return Ok(MapToWorkerResponse(job));
    }

    /// <summary>
    /// Worker reports progress on a claimed job.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="request">Progress update.</param>
    /// <param name="claimToken">The active claim incarnation.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/progress")]
    [WorkerApiKeySecurity] // Public to JWT auth because the claimed worker supplies its key, identity, and lease.
    public async Task<IActionResult> ReportProgressAsync(
        Guid id,
        [FromBody] SliceJobProgressUpdateRequest request,
        [FromHeader(Name = WorkerClaimHeaders.ClaimToken)] Guid claimToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        (WorkerJobLease? lease, IActionResult? failure) = await AuthorizeWorkerMutationAsync(id, ct);
        if (failure is not null)
        {
            return failure;
        }

        string progressMessage = GetPublicProgressMessage(request.ProgressPercent);
        bool updated = await _jobRepository.TryUpdateProgressForActiveLeaseAsync(
            id,
            lease!.Worker.Id,
            claimToken,
            request.ProgressPercent,
            progressMessage,
            ct);
        if (!updated)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

        SliceJob? updatedJob = await _jobRepository.GetByIdAsync(id, ct);
        if (updatedJob is not null)
        {
            await _eventService.NotifyJobProgressAsync(updatedJob, ct);
        }

        return NoContent();
    }

    /// <summary>
    /// Worker marks a job as completed.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="request">Completion details.</param>
    /// <param name="claimToken">The active claim incarnation.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/complete")]
    [WorkerApiKeySecurity] // Public to JWT auth because the claimed worker supplies its key, identity, and lease.
    public async Task<IActionResult> CompleteAsync(
        Guid id,
        [FromBody] CompleteSliceJobRequest request,
        [FromHeader(Name = WorkerClaimHeaders.ClaimToken)] Guid claimToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // JsonStringEnumConverter accepts raw numeric tokens on input (e.g. `"layoutDegradation": 123`),
        // so a malformed or malicious worker payload can bind to an out-of-domain enum value that was
        // never a named member. Refuse it outright rather than persisting/round-tripping it: it must
        // never reach the database as a value that looks valid but Enum.TryParse would silently accept
        // again when read back (see ParseLayoutDegradationReason below).
        if (request.LayoutDegradation is { } requestedLayoutDegradation && !Enum.IsDefined(requestedLayoutDegradation))
        {
            return SlicerApiProblems.InvalidRequest(
                this,
                "invalid_layout_degradation",
                "layoutDegradation must be one of the recognized LayoutDegradationReason values.");
        }

        (WorkerJobLease? lease, IActionResult? failure) = await AuthorizeWorkerMutationAsync(id, ct);
        if (failure is not null)
        {
            return failure;
        }

        SliceJob job = lease!.Job;
        Worker worker = lease.Worker;

        // The worker must prove it wrote the exact profiles that were delivered to it.
        if (!ProfileHashesMatch(job, request))
        {
            return SlicerApiProblems.InvalidRequest(
                this,
                "profile_hash_mismatch",
                "The reported profile digests do not match the profiles delivered with the claim.");
        }

        SliceJob? activeJob = await _jobRepository.GetByActiveWorkerLeaseAsync(
            id,
            worker.Id,
            claimToken,
            ct);
        if (activeJob is null)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

        _ = activeJob;
        var artifactIds = new List<Guid> { request.PrimaryArtifactId };
        if (request.AdditionalArtifactIds is { Length: > 0 })
        {
            artifactIds.AddRange(request.AdditionalArtifactIds);
        }

        if (artifactIds.Count != artifactIds.Distinct().Count())
        {
            return BadRequest(new { error = "Artifact identifiers must be unique." });
        }

        var artifacts = new List<Artifact>(artifactIds.Count);
        foreach (Guid artifactId in artifactIds)
        {
            Artifact? artifact = await _artifactsService.GetAsync(artifactId, ct);
            if (artifact is null ||
                artifact.JobId != id ||
                artifact.WorkerId != worker.Id ||
                artifact.ClaimToken != claimToken)
            {
                return BadRequest(new { error = "One or more artifacts are invalid for this job." });
            }

            artifacts.Add(artifact);
        }

        string resultFileUrl = BuildArtifactDownloadRoute(request.PrimaryArtifactId);

        bool completionApplied = await _jobRepository.TryCompleteForActiveLeaseAsync(
            id,
            worker.Id,
            claimToken,
            resultFileUrl,
            artifactIds,
            request.EstimatedPrintTimeSeconds,
            request.FilamentUsedGrams,
            request.MachineProfileSha256,
            request.ProcessProfileSha256,
            request.FilamentProfileSha256,
            request.LayoutDegradation?.ToString(),
            ct);
        if (!completionApplied)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

        // Re-fetch updated job for event notification
        SliceJob? completed = await _jobRepository.GetByIdAsync(id, ct);
        if (completed is not null)
        {
            await _eventService.NotifyJobCompletedAsync(completed, ct);
        }

        if (_circuitBreaker is not null && completed?.WorkerId is { } successWorkerId)
        {
            await _circuitBreaker.RecordJobSuccessAsync(successWorkerId, _workerRepository, ct);
        }

        _metrics.RecordJobCompletion(artifactIds.Count, hasLog: false);

        return Ok(new CompleteSliceJobResponse
        {
            JobId = id,
            Status = SliceJobStatus.Completed,
            CompletedAt = completed?.CompletedAt,
            ResultFileUrl = resultFileUrl,
            ArtifactIds = artifactIds.ToArray(),
            EstimatedPrintTimeSeconds = request.EstimatedPrintTimeSeconds,
            FilamentUsedGrams = request.FilamentUsedGrams,
            LogArtifactId = null,
            ArtifactsCount = artifactIds.Count,
            ArtifactsTotalBytes = artifacts.Sum(artifact => artifact.SizeBytes),
        });
    }

    /// <summary>
    /// Worker marks a job as failed.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="request">Failure details.</param>
    /// <param name="claimToken">The active claim incarnation.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/fail")]
    [WorkerApiKeySecurity] // Public to JWT auth because the claimed worker supplies its key, identity, and lease.
    public async Task<IActionResult> FailAsync(
        Guid id,
        [FromBody] FailSliceJobRequest request,
        [FromHeader(Name = WorkerClaimHeaders.ClaimToken)] Guid claimToken,
        CancellationToken ct)
    {
        (WorkerJobLease? lease, IActionResult? failure) = await AuthorizeWorkerMutationAsync(id, ct);
        if (failure is not null)
        {
            return failure;
        }

        string errorMessage = string.IsNullOrWhiteSpace(request.ErrorMessage)
            ? "Slicing worker reported a failure."
            : request.ErrorMessage;

        // Only a currently-defined enum member is persisted, so a worker running a different build
        // cannot store a value this API would later hand back to a client (issue #1811).
        string? failureReason = request.FailureReason is SliceFailureReason reported && Enum.IsDefined(reported)
            ? reported.ToString()
            : null;

        bool failed = await _jobRepository.TryFailForActiveLeaseAsync(
            id,
            lease!.Worker.Id,
            claimToken,
            errorMessage,
            failureReason,
            ct);
        if (!failed)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

        SliceJob? failedJob = await _jobRepository.GetByIdAsync(id, ct);
        if (failedJob is not null)
        {
            await _eventService.NotifyJobFailedAsync(failedJob, ct);
        }

        if (_circuitBreaker is not null)
        {
            await _circuitBreaker.RecordJobFailureAsync(lease!.Worker.Id, _workerRepository, ct);
        }

        return Ok(new CompleteSliceJobResponse
        {
            JobId = id,
            Status = SliceJobStatus.Failed,
        });
    }

    /// <summary>
    /// Worker renews the lease on an active job.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="request">Lease renewal request.</param>
    /// <param name="claimToken">The active claim incarnation.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/renew-lease")]
    [WorkerApiKeySecurity] // Public to JWT auth because the claimed worker supplies its key, identity, and lease.
    public async Task<IActionResult> RenewLeaseAsync(
        Guid id,
        [FromBody] RenewLeaseRequest request,
        [FromHeader(Name = WorkerClaimHeaders.ClaimToken)] Guid claimToken,
        CancellationToken ct)
    {
        (WorkerJobLease? lease, IActionResult? failure) = await AuthorizeWorkerMutationAsync(id, ct);
        if (failure is not null)
        {
            return failure;
        }

        bool renewed = await _jobRepository.RenewLeaseAsync(
            id,
            lease!.Worker.Id,
            claimToken,
            request.LeaseDurationSeconds,
            ct);
        if (renewed)
        {
            return NoContent();
        }

        return await GetLeaseFenceFailureAsync(id, ct);
    }

    /// <summary>Downloads the model assigned to the authenticated worker for a claimed job.</summary>
    [HttpGet("{id}/model")]
    [WorkerApiKeySecurity] // Public to JWT auth because the worker supplies its key, identity, and claim token.
    public Task<IActionResult> DownloadWorkerModelAsync(
        Guid id,
        [FromHeader(Name = WorkerClaimHeaders.ClaimToken)] Guid claimToken,
        CancellationToken ct) =>
        DownloadWorkerModelAsync(id, modelIndex: null, claimToken, ct);

    /// <summary>Downloads one model from a multi-model job assigned to the authenticated worker.</summary>
    [HttpGet("{id}/models/{modelIndex:int}")]
    [WorkerApiKeySecurity] // Public to JWT auth because the worker supplies its key, identity, and claim token.
    public Task<IActionResult> DownloadWorkerModelAsync(
        Guid id,
        int modelIndex,
        [FromHeader(Name = WorkerClaimHeaders.ClaimToken)] Guid claimToken,
        CancellationToken ct) =>
        DownloadWorkerModelAsync(id, modelIndex: (int?)modelIndex, claimToken, ct);

    private async Task<IActionResult> DownloadWorkerModelAsync(
        Guid id,
        int? modelIndex,
        Guid claimToken,
        CancellationToken ct)
    {
        (WorkerJobLease? lease, IActionResult? failure) = await AuthorizeWorkerMutationAsync(id, ct);
        if (failure is not null)
        {
            return failure;
        }

        Worker worker = lease!.Worker;
        SliceJob? job = await _jobRepository.GetByActiveWorkerLeaseAsync(
            id,
            worker.Id,
            claimToken,
            ct);
        if (job is null)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

        // Canonical path: bytes are resolved by stored identity through an ownership-checked
        // resolver. A caller-supplied URL is never dereferenced.
        if (modelIndex is null && job.Model3DId is { } model3DId)
        {
            return await DownloadStoredWorkerModelAsync(
                id,
                job,
                worker.Id,
                claimToken,
                model3DId,
                job.ModelSha256,
                ct);
        }

        // Legacy path for pre-existing non-calibration jobs that only recorded a storage key.
        string modelUrl = job.ModelFileUrl;
        string modelFileName = job.ModelFileName;
        if (modelIndex is not null)
        {
            List<string>? modelUrls = DeserializeJsonList<string>(job.ModelFileUrlsJson);
            if (modelUrls is null || modelIndex.Value < 0 || modelIndex.Value >= modelUrls.Count)
            {
                return NotFound();
            }

            modelUrl = modelUrls[modelIndex.Value];
            modelFileName = Uri.TryCreate(modelUrl, UriKind.Absolute, out Uri? modelUri)
                ? Path.GetFileName(modelUri.LocalPath)
                : Path.GetFileName(modelUrl);
        }

        if (TryGetStoredModelId(modelUrl, out Guid storedModelId))
        {
            return await DownloadStoredWorkerModelAsync(
                id,
                job,
                worker.Id,
                claimToken,
                storedModelId,
                expectedSha256: null,
                ct);
        }

        if (_fileStorage is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            Stream model = await _fileStorage.DownloadFileAsync(modelUrl, ct);
            SliceJob? authorizedAfterOpen = await _jobRepository.GetByActiveWorkerLeaseAsync(
                id,
                worker.Id,
                claimToken,
                ct);
            if (authorizedAfterOpen is null)
            {
                await model.DisposeAsync();
                return await GetLeaseFenceFailureAsync(id, ct);
            }

            return File(model, "application/octet-stream", SanitizeFileName(modelFileName, "model.stl"));
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }
    }

    private async Task<IActionResult> DownloadStoredWorkerModelAsync(
        Guid jobId,
        SliceJob job,
        Guid workerId,
        Guid claimToken,
        Guid model3DId,
        string? expectedSha256,
        CancellationToken ct)
    {
        if (_modelStorage is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        ModelResolutionResult resolution = await _modelStorage.OpenAsync(
            model3DId,
            job.UserId,
            expectedSha256,
            ct);
        if (!resolution.Succeeded)
        {
            return resolution.Failure switch
            {
                ModelResolutionFailure.Forbidden => SlicerApiProblems.ResourceForbidden(this),
                ModelResolutionFailure.HashMismatch => Conflict(new
                {
                    error = "The stored model no longer matches its recorded hash.",
                    code = "model_hash_mismatch",
                }),
                _ => NotFound(),
            };
        }

        ResolvedModelContent content = resolution.Content!;
        SliceJob? authorizedAfterOpen = await _jobRepository.GetByActiveWorkerLeaseAsync(
            jobId,
            workerId,
            claimToken,
            ct);
        if (authorizedAfterOpen is null)
        {
#pragma warning disable IDISP007 // The resolver transfers ownership of the returned stream to this action.
            await content.Content.DisposeAsync();
#pragma warning restore IDISP007
            return await GetLeaseFenceFailureAsync(jobId, ct);
        }

        return File(content.Content, content.ContentType, content.FileName);
    }

    private static bool TryGetStoredModelId(string modelUrl, out Guid model3DId)
    {
        model3DId = Guid.Empty;
        string path = Uri.TryCreate(modelUrl, UriKind.Absolute, out Uri? absoluteUri)
            ? absoluteUri.AbsolutePath
            : modelUrl.Split('?', 2)[0];
        const string routeMarker = "/api/3d-models/file/";
        int routeIndex = path.LastIndexOf(routeMarker, StringComparison.OrdinalIgnoreCase);
        if (routeIndex < 0)
        {
            return false;
        }

        string candidate = path[(routeIndex + routeMarker.Length)..].Trim('/');
        return !candidate.Contains('/')
            && Guid.TryParse(candidate, out model3DId);
    }

    /// <summary>
    /// True when the caller is a Desktop-exchange token that must be refused stored-model access.
    /// See <see cref="Farm.Infrastructure.Authorization.DesktopScopeClaims.IsMissingModelScope"/>
    /// for the full rationale (issue #1770 follow-up); this thin wrapper only binds it to the
    /// controller's <see cref="ControllerBase.User"/> so every call site here stays terse.
    /// </summary>
    private bool IsDesktopTokenMissingModelScope() =>
        Farm.Infrastructure.Authorization.DesktopScopeClaims.IsMissingModelScope(User);

    /// <summary>Uploads a verified artifact for a job owned by the authenticated worker.</summary>
    /// <param name="id">The claimed job ID.</param>
    /// <param name="file">The artifact bytes.</param>
    /// <param name="claimToken">The active claim incarnation.</param>
    /// <param name="kind">Canonical artifact kind; defaults to <c>gcode</c>.</param>
    /// <param name="sha256">SHA-256 (hex) the worker computed over the bytes it is sending.</param>
    /// <param name="sizeBytes">Byte count the worker computed over the bytes it is sending.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/artifacts")]
    [WorkerApiKeySecurity] // Public to JWT auth because the claimed worker supplies its key, identity, and lease.
    public async Task<IActionResult> UploadWorkerArtifactAsync(
        Guid id,
        IFormFile file,
        [FromHeader(Name = WorkerClaimHeaders.ClaimToken)] Guid claimToken,
        [FromForm] string? kind,
        [FromForm] string? sha256,
        [FromForm] long? sizeBytes,
        CancellationToken ct)
    {
        (WorkerJobLease? lease, IActionResult? failure) = await AuthorizeWorkerMutationAsync(id, ct);
        if (failure is not null)
        {
            return failure;
        }

        SliceJob? job = await _jobRepository.GetByActiveWorkerLeaseAsync(
            id,
            lease!.Worker.Id,
            claimToken,
            ct);
        if (job is null)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

        try
        {
            string artifactKind = string.IsNullOrWhiteSpace(kind) ? SlicerArtifactKinds.Gcode : kind.Trim();
            Artifact? artifact = await _artifactsService.UploadVerifiedForActiveLeaseAsync(
                file,
                id,
                lease.Worker.Id,
                claimToken,
                artifactKind,
                sha256,
                sizeBytes,
                ct);
            if (artifact is null)
            {
                return await GetLeaseFenceFailureAsync(id, ct);
            }

            return Created(BuildArtifactDownloadRoute(artifact.Id), new
            {
                id = artifact.Id,
                jobId = artifact.JobId,
                kind = artifact.Kind,
                fileName = artifact.FileName,
                contentType = artifact.ContentType,
                sizeBytes = artifact.SizeBytes,
                createdAt = artifact.CreatedAt,
                downloadUrl = BuildArtifactDownloadRoute(artifact.Id),
            });
        }
        catch (ArtifactValidationException exception)
        {
            _logger.LogWarning(
                "Rejected artifact upload for job {JobId} ({Code})",
                id,
                exception.Code);
            return SlicerApiProblems.InvalidRequest(this, exception.Code, exception.Message);
        }
        catch (InvalidOperationException)
        {
            return BadRequest(new { error = "The artifact is empty, too large, or unsupported." });
        }
    }

    /// <summary>
    /// Cancels a slice job.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/cancel")]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
    public async Task<IActionResult> CancelAsync(Guid id, CancellationToken ct)
    {
        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is null)
        {
            return SlicerApiProblems.ResourceNotFound(this);
        }

        if (!CanAccess(job))
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        if (job.Status is SliceJobStatus.Completed or SliceJobStatus.Failed or SliceJobStatus.Cancelled)
        {
            return BadRequest(new { error = $"Job is already in terminal state: {job.Status}" });
        }

        await _jobRepository.UpdateStatusAsync(id, SliceJobStatus.Cancelled, null, null, ct);

        job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is not null)
        {
            await _eventService.NotifyJobCancelledAsync(job, ct);
        }

        return NoContent();
    }

    /// <summary>
    /// Retries a failed slice job by requeuing it.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/retry")]
    [Authorize]
    public async Task<IActionResult> RetryAsync(Guid id, CancellationToken ct)
    {
        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is null)
        {
            return NotFound();
        }

        string currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        if (!Guid.TryParse(currentUserId, out Guid userId) || job.UserId != userId)
        {
            return Forbid();
        }

        if (job.Status is not SliceJobStatus.Failed and not SliceJobStatus.Cancelled)
        {
            return BadRequest(new { error = $"Only failed or cancelled jobs can be retried. Current status: {job.Status}" });
        }

        SliceJob? retriedJob = await _jobRepository.TryRetryJobAsync(
            id,
            userId,
            job.Status,
            job.UpdatedAt,
            ct);
        if (retriedJob is null)
        {
            return Conflict(new { error = "The job changed before it could be retried. Refresh and try again." });
        }

        await _eventService.NotifyJobQueuedAsync(retriedJob, ct);
        return Ok(MapToPublicStatusResponse(retriedJob));
    }

    /// <summary>
    /// Gets worker circuit breaker states.
    /// </summary>
    [HttpGet("circuit-breakers")]
    [Authorize]
    [RequirePermission(PrintFarmerPermissions.DispatchSettings.Manage)]
    public IActionResult GetCircuitBreakerStates()
    {
        if (_circuitBreaker is null)
        {
            return Ok(new { enabled = false });
        }

        _circuitBreaker.CheckCircuits();
        return Ok(new { enabled = true });
    }

    /// <summary>
    /// Ensures the <c>extruderFilamentProfileNames</c> array is present inside SlicerProfileJson
    /// so workers can resolve per-extruder filament profiles from a single JSON blob.
    /// </summary>
    private static string? EmbedExtruderFilamentNames(string? slicerProfileJson, List<string>? names)
    {
        if (names is not { Count: > 0 })
        {
            return slicerProfileJson;
        }

        if (string.IsNullOrWhiteSpace(slicerProfileJson))
        {
            return JsonSerializer.Serialize(new { extruderFilamentProfileNames = names });
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(slicerProfileJson);
            if (doc.RootElement.TryGetProperty("extruderFilamentProfileNames", out _))
            {
                return slicerProfileJson;
            }

            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(slicerProfileJson) ?? [];
            dict["extruderFilamentProfileNames"] = JsonSerializer.SerializeToElement(names);
            return JsonSerializer.Serialize(dict);
        }
        catch (JsonException)
        {
            return slicerProfileJson;
        }
    }

    private bool CanAccess(SliceJob job)
    {
        if (_resourceAccess is not null)
        {
            return _resourceAccess.CanAccess(User, job.UserId, "slice-job", job.Id);
        }

        if (PrintFarmerPermissions.IsFarmAdmin(User))
        {
            LogAdminBypass("slice-job", job.Id);
            return true;
        }

        return PrintFarmerPermissions.TryGetUserId(User, out Guid userId) &&
               userId == job.UserId;
    }

    private void LogAdminBypass(string resourceType, Guid resourceId)
    {
        PrintFarmerPermissions.TryGetUserId(User, out Guid userId);
        _logger.LogInformation(
            "Audited farm-admin resource bypass by user {UserId} for {ResourceType} {ResourceId}",
            userId,
            resourceType,
            resourceId);
    }

    private SliceJobStatusResponse MapToPublicStatusResponse(SliceJob job)
    {
        bool isAdmin = PrintFarmerPermissions.IsFarmAdmin(User);
        return new()
        {
            Id = job.Id,
            Status = job.Status,
            ProgressPercent = job.ProgressPercent,
            ProgressMessage = job.Status == SliceJobStatus.Processing
                ? GetPublicProgressMessage(job.ProgressPercent)
                : null,
            QueuedAt = job.QueuedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            ErrorMessage = string.IsNullOrWhiteSpace(job.ErrorMessage) ? null : "Slicing failed.",

            // Non-admins never see the real detail (which may include worker container paths or
            // internal file names); admins get it verbatim so they can diagnose failures like the
            // profile-resolution error in issue #1768 without shelling into a worker.
            ErrorDetail = isAdmin && !string.IsNullOrWhiteSpace(job.ErrorMessage) ? job.ErrorMessage : null,
            LayoutDegradation = ParseLayoutDegradationReason(job.LayoutDegradationReason),

            // Safe for every caller by construction: FailureReason is a closed enum and FailureHint
            // is a constant string looked up from it, so neither can carry worker paths, filenames
            // or CLI arguments the way ErrorDetail can (issue #1811).
            //
            // Gated on the terminal failed status as well as being cleared on retry: the reason
            // describes one attempt, so a job that has been requeued or has since succeeded must
            // never still be explaining a previous failure.
            FailureReason = job.Status == SliceJobStatus.Failed
                ? ParseFailureReason(job.FailureReason)
                : null,
            FailureHint = job.Status == SliceJobStatus.Failed &&
                          ParseFailureReason(job.FailureReason) is SliceFailureReason reason
                ? SliceFailureHints.For(reason)
                : null,
            EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
            FilamentUsedGrams = job.FilamentUsedGrams,
            WorkerId = null,
            ModelFileName = SanitizeFileName(job.ModelFileName, "model"),
            SlicerEngine = SlicerEngineNames.Resolve(job),
            ArtifactsRoute = $"/api/artifacts/job/{job.Id}",
        };
    }

    /// <summary>
    /// Parses the persisted <see cref="SliceJob.LayoutDegradationReason"/> enum-name column back
    /// into the typed contract value. Returns <see langword="null"/> for an unset or unrecognized
    /// value instead of throwing, since this is a redacted signal, not authoritative state.
    /// </summary>
    /// <remarks>
    /// <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> alone is not sufficient: it also
    /// accepts a bare numeric string (e.g. <c>"123"</c>) and happily returns an out-of-domain enum
    /// value for it, because C# enums can hold any underlying-type value regardless of whether it
    /// names a member. <see cref="Enum.IsDefined{TEnum}(TEnum)"/> closes that gap so a value that was
    /// never a real degradation reason - however it ended up in the column - is never surfaced as one.
    /// </remarks>
    private static LayoutDegradationReason? ParseLayoutDegradationReason(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Enum.TryParse(value, ignoreCase: true, out LayoutDegradationReason reason) &&
        Enum.IsDefined(reason)
            ? reason
            : null;

    /// <summary>
    /// Parses the persisted <see cref="SliceFailureReason"/> name, rejecting anything that is not a
    /// currently-defined member so a value written by a differently-versioned worker can never be
    /// echoed back to a caller.
    /// </summary>
    /// <param name="value">The persisted enum name.</param>
    /// <returns>The parsed reason, or <see langword="null"/>.</returns>
    private static SliceFailureReason? ParseFailureReason(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Enum.TryParse(value, ignoreCase: true, out SliceFailureReason reason) &&
        Enum.IsDefined(reason)
            ? reason
            : null;

    private static WorkerSliceJobResponse MapToWorkerResponse(SliceJob job)
    {
        List<string>? modelUrls = DeserializeJsonList<string>(job.ModelFileUrlsJson);
        return new WorkerSliceJobResponse
        {
            Id = job.Id,
            ClaimToken = job.ClaimToken
                ?? throw new InvalidOperationException("A claimed worker job must have a claim token."),
            UserId = job.UserId,
            PrinterId = job.PrinterId,
            Status = job.Status,
            ModelFileUrl = $"/api/slice/{job.Id}/model",
            ModelFileName = SanitizeFileName(job.ModelFileName, "model.stl"),
            ModelSha256 = job.ModelSha256,
            SlicerEngine = SlicerEngineNames.Resolve(job),
            SlicerProfileJson = job.SlicerProfileJson,
            ModelTransformJson = job.ModelTransformJson,
            ModelFileUrls = modelUrls?
                .Select((_, index) => $"/api/slice/{job.Id}/models/{index}")
                .ToList(),
            ModelFileTransforms = DeserializeJsonList<string?>(job.ModelFileTransformsJson),
            MachineProfileJson = job.MachineProfileJson,
            ProcessProfileJson = job.ProcessProfileJson,
            FilamentProfileJson = job.FilamentProfileJson,
            MachineProfileSha256 = job.MachineProfileSha256,
            ProcessProfileSha256 = job.ProcessProfileSha256,
            FilamentProfileSha256 = job.FilamentProfileSha256,
            SlicerDistribution = job.SlicerDistribution,
            SlicerVersion = job.SlicerVersion,
            SlicerContainerDigest = job.SlicerContainerDigest,
            LeaseToken = job.LeaseToken ?? Guid.Empty,
            LeaseFence = job.LeaseFence,
            LeaseExpiresAtUtc = job.LeaseExpiresAt,
            RequiredCapabilitiesJson = job.RequiredCapabilitiesJson,
            Priority = job.Priority,
            CalibrationMethod = job.CalibrationMethod,
            CalibrationParamsJson = job.CalibrationParamsJson,
        };
    }

    private static List<T>? DeserializeJsonList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<Worker?> GetAuthorizedWorkerAsync()
    {
        return await _workerAuth.AuthenticateAsync(HttpContext);
    }

    private async Task<IActionResult> GetLeaseFenceFailureAsync(Guid jobId, CancellationToken ct)
    {
        SliceJob? job = await _jobRepository.GetByIdAsync(jobId, ct);
        return job is null
            ? NotFound()
            : SlicerApiProblems.ResourceForbidden(this);
    }

    /// <summary>
    /// Validates that the caller is an authenticated worker holding an unexpired, current-fence lease
    /// on the requested job and that it is capable of the job's engine.
    /// </summary>
    /// <param name="jobId">The job the caller wants to mutate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The validated lease, or the problem result to return. Exactly one of the two is non-null.
    /// </returns>
    private async Task<(WorkerJobLease? Lease, IActionResult? Failure)> AuthorizeWorkerMutationAsync(
        Guid jobId,
        CancellationToken ct)
    {
        Worker? worker = await GetAuthorizedWorkerAsync();
        if (worker is null)
        {
            return (null, SlicerApiProblems.AuthenticationRequired(this));
        }

        SliceJob? job = await _jobRepository.GetByIdAsync(jobId, ct);
        if (job is null)
        {
            return (null, NotFound());
        }

        if (job.WorkerId != worker.Id || job.Status != SliceJobStatus.Processing)
        {
            return (null, SlicerApiProblems.ResourceForbidden(this));
        }

        if (!WorkerSupportsEngine(worker, SlicerEngineNames.Resolve(job)))
        {
            return (null, SlicerApiProblems.ResourceForbidden(this));
        }

        if (job.LeaseToken is not { } storedToken)
        {
            // A claimed job without a lease token predates fencing; it must be re-claimed rather
            // than mutated, because its holder cannot be proven.
            return (null, SlicerApiProblems.LeaseConflict(this, "lease_required"));
        }

        if (job.LeaseExpiresAt is not { } expiresAt || expiresAt <= DateTime.UtcNow)
        {
            return (null, SlicerApiProblems.LeaseConflict(this, "lease_expired"));
        }

        if (!TryReadLeaseHeaders(out Guid presentedToken, out long presentedFence))
        {
            return (null, SlicerApiProblems.LeaseConflict(this, "lease_required"));
        }

        if (presentedToken != storedToken)
        {
            return (null, SlicerApiProblems.LeaseConflict(this, "lease_conflict"));
        }

        if (presentedFence != job.LeaseFence)
        {
            return (null, SlicerApiProblems.LeaseConflict(this, "stale_fencing_token"));
        }

        return (new WorkerJobLease(worker, job, storedToken, job.LeaseFence), null);
    }

    private bool TryReadLeaseHeaders(out Guid leaseToken, out long leaseFence)
    {
        leaseToken = Guid.Empty;
        leaseFence = 0;

        // Lease fencing is a transport-level worker concern that applies uniformly to JSON and
        // multipart routes, so it is read from headers rather than bound per action model.
#pragma warning disable S6932 // Header-based lease fencing is intentional for the worker contract
        return Request.Headers.TryGetValue(WorkerLeaseHeaders.LeaseToken, out Microsoft.Extensions.Primitives.StringValues tokenValues) &&
               Guid.TryParse(tokenValues.FirstOrDefault(), out leaseToken) &&
               leaseToken != Guid.Empty &&
               Request.Headers.TryGetValue(WorkerLeaseHeaders.LeaseFence, out Microsoft.Extensions.Primitives.StringValues fenceValues) &&
               long.TryParse(
                   fenceValues.FirstOrDefault(),
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out leaseFence);
#pragma warning restore S6932
    }

    /// <summary>
    /// Confirms the worker advertises the capability tag required by the job's engine.
    /// </summary>
    /// <param name="worker">The authenticated worker.</param>
    /// <param name="engine">The engine the job requires.</param>
    /// <returns><see langword="true"/> when the worker advertises the engine capability.</returns>
    private static bool WorkerSupportsEngine(Worker worker, SlicerEngineType engine)
    {
        string tag = SlicerEngineNames.ToCapabilityTag(engine);
        if (string.IsNullOrWhiteSpace(worker.CapabilitiesJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(worker.CapabilitiesJson);
            JsonElement capabilities = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement,
                JsonValueKind.Object when document.RootElement.TryGetProperty("capabilities", out JsonElement value) => value,
                _ => default,
            };

            return capabilities.ValueKind == JsonValueKind.Array &&
                   capabilities.EnumerateArray().Any(value =>
                       value.ValueKind == JsonValueKind.String &&
                       string.Equals(value.GetString(), tag, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ProfileHashesMatch(SliceJob job, CompleteSliceJobRequest request)
    {
        string?[] expected =
        [
            job.MachineProfileSha256,
            job.ProcessProfileSha256,
            job.FilamentProfileSha256,
        ];
        string?[] reported =
        [
            request.MachineProfileSha256,
            request.ProcessProfileSha256,
            request.FilamentProfileSha256,
        ];

        if (expected.Any(value => !string.IsNullOrWhiteSpace(value)))
        {
            return expected.All(IsSha256) &&
                   HashMatches(expected[0], reported[0]) &&
                   HashMatches(expected[1], reported[1]) &&
                   HashMatches(expected[2], reported[2]);
        }

        return reported.All(string.IsNullOrWhiteSpace) || reported.All(IsSha256);
    }

    /// <summary>
    /// Compares an expected digest with the digest a worker reported.
    /// </summary>
    /// <param name="expected">Digest recorded on the job, or <see langword="null"/> when no profile was delivered.</param>
    /// <param name="reported">Digest the worker echoed back.</param>
    /// <returns>
    /// <see langword="true"/> when no profile was delivered, or when the reported digest matches.
    /// A delivered profile with a missing or different reported digest never matches.
    /// </returns>
    private static bool HashMatches(string? expected, string? reported)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(reported) &&
               string.Equals(expected.Trim(), reported.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string? value) =>
        value is not null &&
        value.Trim() is { Length: 64 } trimmed &&
        trimmed.All(char.IsAsciiHexDigit);

    /// <summary>
    /// Builds the canonical authenticated download route for an artifact.
    /// </summary>
    /// <param name="artifactId">The artifact identity.</param>
    /// <returns>The permission-checked API route callers use to download the bytes.</returns>
    private static string BuildArtifactDownloadRoute(Guid artifactId) =>
        $"/api/artifacts/{artifactId}";

    /// <summary>
    /// Binds the submission to stored model identity, refusing caller-supplied locations for
    /// canonical jobs.
    /// </summary>
    private async Task<IActionResult?> BindStoredModelAsync(
        SliceJob job,
        SubmitSliceJobRequest request,
        Guid userId,
        CancellationToken ct)
    {
        if (request.Model3DId is not { } model3DId)
        {
            if (string.IsNullOrWhiteSpace(request.ModelFileUrl))
            {
                return SlicerApiProblems.InvalidRequest(
                    this,
                    "model_reference_required",
                    "Supply model3DId for stored models, or modelFileUrl for a previously stored key.");
            }

            // The legacy modelFileUrl form can also reference a stored library model
            // ("/api/3d-models/file/{id}") rather than an external location. The worker later
            // dereferences that URL via TryGetStoredModelId with no per-caller scope check, so this
            // path needs the identical Desktop-scope guard as the model3DId path below or a
            // submit-only-scoped Desktop token could route around it (issue #1770 follow-up).
            if (TryGetStoredModelId(request.ModelFileUrl, out _) && IsDesktopTokenMissingModelScope())
            {
                return SlicerApiProblems.ResourceForbidden(this);
            }

            return null;
        }

        if (_modelStorage is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // The class-level slicing:submit permission is intentionally broad enough that a
        // Desktop-exchange token issued only for calibration generation (issue #838) legitimately
        // holds it. Since #1770 made model3DId lookups succeed for any existing library model
        // rather than just the caller's own uploads, a submit-only-scoped Desktop token could
        // otherwise reference (and thereby read the contents of) an arbitrary model it was never
        // granted ModelRead/LibrarySync access to. Normal login/session tokens - which carry no
        // DesktopScopeClaims.TokenUse claim - are unaffected, exactly as DesktopScopeAuthorizationHandler
        // behaves for attribute-gated endpoints.
        if (IsDesktopTokenMissingModelScope())
        {
            return SlicerApiProblems.ResourceForbidden(this);
        }

        Model3D? model = await _modelStorage.FindOwnedAsync(model3DId, userId, ct);
        if (model is null)
        {
            return SlicerApiProblems.InvalidRequest(
                this,
                "model_not_found",
                "The referenced stored model does not exist or is not accessible.");
        }

        job.Model3DId = model.Id;
        job.ModelSha256 = string.IsNullOrWhiteSpace(model.FileHash) ? null : model.FileHash;

        // The worker resolves bytes through the authenticated model route, so the free-form caller
        // location is replaced rather than persisted for dereference.
        job.ModelFileUrl = $"/api/slice/{job.Id}/model";
        if (string.IsNullOrWhiteSpace(job.ModelFileName))
        {
            job.ModelFileName = string.IsNullOrWhiteSpace(model.Name) ? model.FileName : model.Name;
        }

        return null;
    }

    /// <summary>
    /// Snapshots exact native upstream-Orca profile JSON and hashes onto the job before it can be
    /// claimed, so the worker receives immutable content rather than a live lookup.
    /// </summary>
    private async Task<IActionResult?> BindResolvedProfilesAsync(
        SliceJob job,
        SubmitSliceJobRequest request,
        Guid userId,
        CancellationToken ct)
    {
        if (request.MachineProfileId is not { } machineId ||
            request.ProcessProfileId is not { } processId ||
            request.FilamentProfileId is not { } filamentId)
        {
            bool anySupplied = request.MachineProfileId.HasValue ||
                               request.ProcessProfileId.HasValue ||
                               request.FilamentProfileId.HasValue;
            return anySupplied
                ? SlicerApiProblems.InvalidRequest(
                    this,
                    "incomplete_profile_selection",
                    "Machine, process and filament profiles must all be supplied together.")
                : null;
        }

        if (_profileResolver is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        ResolvedCalibrationProfiles resolved;
        try
        {
            resolved = await _profileResolver.ResolveAsync(
                machineId,
                processId,
                filamentId,
                new CalibrationProfileAccessScope(userId, PrintFarmerPermissions.IsFarmAdmin(User)),
                ct);
        }
        catch (CalibrationProfileResolverUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (resolved.Machine is null || resolved.Process is null || resolved.Filament is null)
        {
            return SlicerApiProblems.InvalidRequest(
                this,
                "profile_not_found",
                "One or more referenced profiles do not exist or are not accessible.");
        }

        if (string.IsNullOrWhiteSpace(resolved.Machine.RawJson) ||
            string.IsNullOrWhiteSpace(resolved.Process.RawJson) ||
            string.IsNullOrWhiteSpace(resolved.Filament.RawJson))
        {
            return SlicerApiProblems.InvalidRequest(
                this,
                "profile_content_unavailable",
                "The referenced profiles do not carry native slicer JSON.");
        }

        // #1846: a profile resolved by ID can be a system/stock profile imported by ProfilesService
        // (manually, or via SystemProfileReconciliationService), whose RawJson is a serialized CLR
        // DTO (MachineProfileDto/ProcessProfileDto/FilamentProfileDto — PascalCase properties plus a
        // top-level "Settings" bag), not the flat native OrcaSlicer document NativeSlicerProfiles
        // promises to deliver verbatim. Snapshotting that DTO envelope directly produces a
        // machine/process/filament.json OrcaSlicer's own config parser rejects at preset-parse time
        // (CLI_CONFIG_FILE_ERROR, exit 251) before slicing ever begins. Unwrap it to the verbatim
        // upstream document preserved in "Settings" instead, mirroring the same shape distinction
        // ResolveNamedProfilesAsync already makes for the name-based submission path.
        string? machineJson = ExtractFlatNativeProfileJson(resolved.Machine.RawJson);
        string? processJson = ExtractFlatNativeProfileJson(resolved.Process.RawJson);
        string? filamentJson = ExtractFlatNativeProfileJson(resolved.Filament.RawJson);

        if (machineJson is null || processJson is null || filamentJson is null)
        {
            return SlicerApiProblems.InvalidRequest(
                this,
                "profile_content_unavailable",
                "The referenced profiles do not carry native slicer JSON.");
        }

        job.MachineProfileJson = machineJson;
        job.ProcessProfileJson = processJson;
        job.FilamentProfileJson = filamentJson;
        job.MachineProfileSha256 = ComputeSha256(machineJson);
        job.ProcessProfileSha256 = ComputeSha256(processJson);
        job.FilamentProfileSha256 = ComputeSha256(filamentJson);
        job.SlicerDistribution = resolved.Machine.SlicerDistribution ?? CalibrationContractConstants.SlicerDistribution;
        job.SlicerVersion = resolved.Machine.SlicerVersion ?? CalibrationContractConstants.SlicerVersion;

        return null;
    }

    /// <summary>
    /// Resolves named machine/process/filament profiles embedded in <c>SlicerProfileJson</c>
    /// against the database (system profiles plus this user's own) and snapshots native JSON onto
    /// the job, mirroring <see cref="BindResolvedProfilesAsync"/>'s ID-based path.
    /// </summary>
    /// <remarks>
    /// The "New Slice Job" UI only ever submits profile selections by name (never by ID), which
    /// routes every real job through the worker's <c>ResolveProfileFromJsonAsync</c> — a lookup
    /// that only knows the worker's own bundled OrcaSlicer resources and has no database access at
    /// all. Any custom/user-owned profile name (e.g. a filament like "FilAr PLA Bronce") can never
    /// resolve there, so the job fails immediately once the worker discovers it has no usable
    /// profile set, before OrcaSlicer is even launched (issue #1768). Resolving names here — where
    /// the database is available — and snapshotting the result the same way the ID-based path does
    /// routes the job onto the already-robust <c>NativeProfiles</c> path instead.
    ///
    /// This only engages when all three names are present and all three resolve; a partial match
    /// is deliberately left alone (rather than partially snapshotted) so the legacy worker-side
    /// resolution remains the fallback for any name that is not yet mirrored into the database.
    /// Multi-extruder submissions (which carry <c>extruderFilamentProfileNames</c>) are also left
    /// alone since <see cref="Models.NativeSlicerProfiles"/> only carries a single filament
    /// document. Process overrides and a single-filament colour override embedded in
    /// <c>SlicerProfileJson</c> are re-applied onto the resolved JSON so this path does not
    /// silently drop user customizations that the legacy path would otherwise have applied.
    /// </remarks>
    private async Task ResolveNamedProfilesAsync(SliceJob job, Guid userId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(job.MachineProfileJson))
        {
            // Already bound via MachineProfileId/ProcessProfileId/FilamentProfileId above.
            return;
        }

        if (string.IsNullOrWhiteSpace(job.SlicerProfileJson) ||
            _machineProfileRepository is null ||
            _processProfileRepository is null ||
            _filamentProfileRepository is null)
        {
            return;
        }

        // Only OrcaSlicer profiles are mirrored into the database today (see ProfilesService);
        // other engines keep using the legacy worker-side name lookup unchanged.
        if ((SlicerEngineType)job.SlicerEngine != SlicerEngineType.OrcaSlicer)
        {
            return;
        }

        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(job.SlicerProfileJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        // Multi-extruder jobs still resolve per-extruder filaments worker-side; leave them alone.
        if (root.TryGetProperty("extruderFilamentProfileNames", out JsonElement extrudersElem) &&
            extrudersElem.ValueKind == JsonValueKind.Array &&
            extrudersElem.GetArrayLength() > 0)
        {
            return;
        }

        string? machineName = GetJsonString(root, "machineProfileName");
        string? processName = GetJsonString(root, "processProfileName");
        string? filamentName = GetJsonString(root, "filamentProfileName");
        if (string.IsNullOrWhiteSpace(machineName) ||
            string.IsNullOrWhiteSpace(processName) ||
            string.IsNullOrWhiteSpace(filamentName))
        {
            return;
        }

        IReadOnlyList<MachineProfile> machines =
            await _machineProfileRepository.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId, ct);
        MachineProfile? machine = machines.FirstOrDefault(m =>
            string.Equals(m.Name, machineName, StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<ProcessProfile> processes =
            await _processProfileRepository.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId, ct);
        List<ProcessProfile> processCandidates = processes
            .Where(p => string.Equals(p.Name, processName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        ProcessProfile? process = SelectCompatibleProfile(
            processCandidates, machine, p => p.CompatiblePrinters, p => p.PrinterModelId);

        IReadOnlyList<FilamentProfile> filaments =
            await _filamentProfileRepository.GetByEngineAsync(SlicerType.OrcaSlicer, includeSystem: true, userId, ct);
        List<FilamentProfile> filamentCandidates = filaments
            .Where(f => string.Equals(f.Name, filamentName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        FilamentProfile? filament = SelectCompatibleProfile(filamentCandidates, machine, f => f.CompatiblePrinters);

        if (machine is null || process is null || filament is null ||
            string.IsNullOrWhiteSpace(machine.RawJson) ||
            string.IsNullOrWhiteSpace(process.RawJson) ||
            string.IsNullOrWhiteSpace(filament.RawJson))
        {
            // Cannot fully replace the legacy path — NativeSlicerProfiles.FromJob requires all
            // three documents, so a partial match is left for the worker-side fallback rather than
            // snapshotting an incomplete set.
            _logger.LogInformation(
                "Named profile snapshot skipped for job {JobId}: machineFound={MachineFound}, processFound={ProcessFound}, filamentFound={FilamentFound}",
                job.Id,
                machine is not null,
                process is not null,
                filament is not null);
            return;
        }

        // System/stock profiles imported by SlicersService store RawJson as a serialized CLR DTO
        // (MachineProfileDto/ProcessProfileDto/FilamentProfileDto — see their "Settings" bag), not
        // the flat native OrcaSlicer document NativeSlicerProfiles/WriteNativeProfilesAsync writes
        // verbatim to disk. Only user-authored custom profiles saved through ProfilesService (which
        // sanitizes RawJson to a flat native document) are safe to snapshot here. Snapshotting a
        // DTO-shaped document would produce a machine/process/filament.json OrcaSlicer cannot parse,
        // regressing stock-name submissions that previously worked via the legacy worker-side
        // resolution. Bail (defer to the legacy path) rather than risk that.
        if (!IsFlatNativeProfileJson(machine.RawJson) ||
            !IsFlatNativeProfileJson(process.RawJson) ||
            !IsFlatNativeProfileJson(filament.RawJson))
        {
            _logger.LogInformation(
                "Named profile snapshot skipped for job {JobId}: one or more resolved profiles are not flat native JSON (likely a system/stock profile stored as a DTO); deferring to the legacy worker-side resolution",
                job.Id);
            return;
        }

        string processJson = ApplyProcessOverrides(process.RawJson, root);
        string filamentJson = ApplyFilamentColourOverride(filament.RawJson, root);

        job.MachineProfileJson = machine.RawJson;
        job.ProcessProfileJson = processJson;
        job.FilamentProfileJson = filamentJson;
        job.MachineProfileSha256 = ComputeSha256(machine.RawJson);
        job.ProcessProfileSha256 = ComputeSha256(processJson);
        job.FilamentProfileSha256 = ComputeSha256(filamentJson);
        job.SlicerDistribution = machine.SlicerDistribution ?? job.SlicerDistribution;
        job.SlicerVersion = machine.SlicerVersion ?? job.SlicerVersion;
    }

    /// <summary>
    /// Disambiguates a set of same-named process/filament profile candidates against the resolved
    /// machine profile, using the same compatibility rules <see cref="ProfilesService"/> applies
    /// when browsing profiles for a selected machine. <c>Name</c> alone is not a unique key for
    /// either <see cref="ProcessProfile"/> or <see cref="FilamentProfile"/> — OrcaSlicer commonly
    /// ships process profiles with the same display name scoped to different printer models via
    /// <c>CompatiblePrinters</c>/<c>PrinterModelId</c> — so picking the first name match could
    /// silently snapshot an arbitrary, possibly incompatible profile onto the job (issue #1778
    /// review finding). Returns <see langword="null"/> when the candidate set is ambiguous and no
    /// candidate can be confidently matched to the resolved machine, so the caller bails to the
    /// legacy worker-side path rather than guessing.
    /// </summary>
    private static T? SelectCompatibleProfile<T>(
        List<T> candidates,
        MachineProfile? machine,
        Func<T, string?> compatiblePrintersSelector,
        Func<T, Guid?>? printerModelIdSelector = null)
        where T : class
    {
        if (candidates.Count <= 1)
        {
            return candidates.Count == 1 ? candidates[0] : null;
        }

        if (machine is not null)
        {
            T? byCompatiblePrinters = candidates.FirstOrDefault(c =>
            {
                string? compatiblePrinters = compatiblePrintersSelector(c);
                return !string.IsNullOrWhiteSpace(compatiblePrinters) &&
                    compatiblePrinters.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Any(cp => string.Equals(cp.Trim(), machine.Name, StringComparison.OrdinalIgnoreCase));
            });
            if (byCompatiblePrinters is not null)
            {
                return byCompatiblePrinters;
            }

            if (printerModelIdSelector is not null && machine.PrinterModelId.HasValue)
            {
                T? byModel = candidates.FirstOrDefault(c => printerModelIdSelector(c) == machine.PrinterModelId);
                if (byModel is not null)
                {
                    return byModel;
                }
            }

            // No candidate explicitly declares itself compatible with the resolved machine, but a
            // candidate that declares no scoping at all (no CompatiblePrinters, no PrinterModelId)
            // is model-agnostic by construction and safe to use.
            T? modelAgnostic = candidates.FirstOrDefault(c =>
                string.IsNullOrWhiteSpace(compatiblePrintersSelector(c)) &&
                (printerModelIdSelector is null || printerModelIdSelector(c) is null));
            if (modelAgnostic is not null)
            {
                return modelAgnostic;
            }
        }

        // Ambiguous: multiple same-named candidates and none can be confidently tied to the
        // resolved machine. Do not guess — defer to the legacy worker-side path.
        return null;
    }

    /// <summary>
    /// Distinguishes a flat native OrcaSlicer profile document (snake_case keys, safe to write
    /// verbatim) from a serialized CLR profile DTO (MachineProfileDto/ProcessProfileDto/
    /// FilamentProfileDto), which always carries a top-level "Settings" bag and promoted
    /// PascalCase properties that OrcaSlicer's own config parser does not understand.
    /// </summary>
    private static bool IsFlatNativeProfileJson(string rawJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                !doc.RootElement.TryGetProperty("Settings", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a stored profile's RawJson to the flat native OrcaSlicer document that
    /// <see cref="Models.NativeSlicerProfiles"/> promises to write verbatim.
    /// </summary>
    /// <remarks>
    /// When <paramref name="rawJson"/> is already flat native (see
    /// <see cref="IsFlatNativeProfileJson"/>) it is returned unchanged. When it is instead a
    /// serialized CLR profile DTO (MachineProfileDto/ProcessProfileDto/FilamentProfileDto —
    /// PascalCase properties plus a top-level "Settings" bag, as produced by
    /// <c>ProfilesService</c>'s system-profile import), the verbatim upstream-Orca document is
    /// unwrapped from that bag and returned instead: the promoted DTO envelope itself is not
    /// something OrcaSlicer's own config parser understands (issue #1846 — every ID-based
    /// submission that resolved to a system/stock profile snapshotted the DTO envelope verbatim
    /// and failed at preset-parse time with <c>CLI_CONFIG_FILE_ERROR</c>, exit 251).
    /// </remarks>
    /// <returns>The flat native document, or <see langword="null"/> when neither shape applies.</returns>
    private static string? ExtractFlatNativeProfileJson(string rawJson)
    {
        if (IsFlatNativeProfileJson(rawJson))
        {
            return rawJson;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("Settings", out JsonElement settingsElem) &&
                settingsElem.ValueKind == JsonValueKind.Object)
            {
                return settingsElem.GetRawText();
            }
        }
        catch (JsonException)
        {
            // Fall through to null below.
        }

        return null;
    }

    private static string? GetJsonString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>
    /// Applies the "overrides" object embedded in SlicerProfileJson onto a resolved native process
    /// profile document, matching the per-key overwrite semantics of the worker's legacy
    /// <c>ResolveProfileFromJsonAsync</c> path so this fast path does not drop user-tuned settings.
    /// </summary>
    /// <remarks>
    /// Compatibility keys are rejected here for the same reason the worker rejects them — see
    /// <see cref="ProcessOverridePolicy"/>. This path snapshots a document the worker writes
    /// verbatim after digest verification, so a compatibility override accepted here would bypass
    /// the worker's own filter entirely.
    /// </remarks>
    /// <param name="rawJson">The resolved native process profile document.</param>
    /// <param name="root">The submission's parsed <c>SlicerProfileJson</c>.</param>
    /// <returns>The document with permitted overrides applied.</returns>
    private static string ApplyProcessOverrides(string rawJson, JsonElement root)
    {
        if (!root.TryGetProperty("overrides", out JsonElement overridesElem) ||
            overridesElem.ValueKind != JsonValueKind.Object ||
            !overridesElem.EnumerateObject().Any())
        {
            return rawJson;
        }

        try
        {
            if (JsonNode.Parse(rawJson) is not JsonObject obj)
            {
                return rawJson;
            }

            foreach (JsonProperty prop in overridesElem.EnumerateObject())
            {
                if (ProcessOverridePolicy.IsRejectedOverrideKey(prop.Name))
                {
                    continue;
                }

                obj[prop.Name] = ToNativeOverrideValue(prop.Value);
            }

            return obj.ToJsonString();
        }
        catch (JsonException)
        {
            return rawJson;
        }
    }

    /// <summary>
    /// Converts a JSON override value to the shape native OrcaSlicer process JSON expects: every
    /// scalar is written as a JSON string (matching <c>HttpJobPollerService</c>'s legacy override
    /// mapping — string as-is, <c>true</c>/<c>false</c> as "1"/"0", numbers as their string form),
    /// with arrays passed through as native arrays. OrcaSlicer's CLI parser rejects a process.json
    /// containing raw numeric/boolean scalars, so preserving the override's original JSON type
    /// (as a naive <c>JsonNode.Parse(GetRawText())</c> would) breaks any numeric or boolean
    /// override even though the resolved base document is otherwise valid.
    /// </summary>
    private static JsonNode? ToNativeOverrideValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => JsonNode.Parse(value.GetRawText()),
        JsonValueKind.String => JsonValue.Create(value.GetString() ?? string.Empty),
        JsonValueKind.True => JsonValue.Create("1"),
        JsonValueKind.False => JsonValue.Create("0"),
        JsonValueKind.Number => JsonValue.Create(value.GetRawText()),
        _ => JsonValue.Create(value.GetRawText())
    };

    /// <summary>
    /// Applies the single-filament "filamentColour" override embedded in SlicerProfileJson onto a
    /// resolved native filament profile document (cosmetic — preview/G-code metadata only).
    /// </summary>
    private static string ApplyFilamentColourOverride(string rawJson, JsonElement root)
    {
        if (!root.TryGetProperty("filamentColour", out JsonElement colourElem) ||
            colourElem.ValueKind != JsonValueKind.String)
        {
            return rawJson;
        }

        string? colour = colourElem.GetString();
        if (string.IsNullOrWhiteSpace(colour))
        {
            return rawJson;
        }

        try
        {
            if (JsonNode.Parse(rawJson) is not JsonObject obj)
            {
                return rawJson;
            }

            obj["filament_colour"] = new JsonArray(JsonValue.Create(colour));
            return obj.ToJsonString();
        }
        catch (JsonException)
        {
            return rawJson;
        }
    }

    /// <summary>Computes the uppercase hexadecimal SHA-256 of a UTF-8 payload.</summary>
    /// <param name="value">The payload to digest.</param>
    /// <returns>The uppercase hexadecimal digest.</returns>
    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string GetPublicProgressMessage(int progressPercent) =>
        $"Slicing in progress ({Math.Clamp(progressPercent, 0, 100)}%).";

    private static string SanitizeFileName(string? fileName, string fallback)
    {
        string baseName = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        string sanitized = NonFileNameCharacterRegex().Replace(baseName, "_");
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    [GeneratedRegex("[^a-zA-Z0-9._-]+")]
    private static partial Regex NonFileNameCharacterRegex();

    /// <summary>A validated worker lease over a claimed job.</summary>
    /// <param name="Worker">The authenticated worker holding the lease.</param>
    /// <param name="Job">The claimed job.</param>
    /// <param name="Token">The lease token both sides agree on.</param>
    /// <param name="Fence">The fencing counter both sides agree on.</param>
    private sealed record WorkerJobLease(Worker Worker, SliceJob Job, Guid Token, long Fence);
}
