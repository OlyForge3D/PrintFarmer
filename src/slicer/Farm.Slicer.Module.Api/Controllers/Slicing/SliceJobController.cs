using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Authorization;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Contracts.Libraries;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Metrics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Api.Controllers.Slicing;

/// <summary>
/// API endpoints for slice job lifecycle management (submit, claim, progress, complete).
/// </summary>
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
    IPrinterAccessValidator? printerAccess = null) : ControllerBase
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
        if (!PrintFarmerPermissions.TryGetUserId(User, out Guid userId))
        {
            return SlicerApiProblems.ResourceForbidden(this);
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

                resolvedModelUrls.Add(resolved);
            }
        }

        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PrinterId = request.PrinterId,
            ModelFileUrl = modelFileUrl,
            ModelFileName = request.ModelFileName,
            SlicerEngine = request.SlicerEngine,
            SlicerProfileJson = EmbedExtruderFilamentNames(request.SlicerProfileJson, request.ExtruderFilamentProfileNames),
            SlicerProfileId = request.SlicerProfileId,

            // Server derives RequiredCapabilitiesJson from the (engine, version) tuple below.
            // Client-supplied values are intentionally ignored so a bad/malicious client cannot
            // force the wrong worker to claim the job (issue #578).
            RequiredCapabilitiesJson = null,
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
        string engineName = ResolveEngineName(request.SlicerEngine);
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

        await _jobRepository.AddAsync(job, ct);
        await _eventService.NotifyJobQueuedAsync(job, ct);

        return Created($"/api/slice/{job.Id}", new SubmitSliceJobResponse
        {
            JobId = job.Id,
            Status = job.Status,
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
            if (!string.IsNullOrEmpty(status))
            {
                jobs = await _jobRepository.GetByStatusAsync(status, limit, ct);
            }
            else
            {
                jobs = await _jobRepository.GetQueuedJobsAsync(limit, ct);
            }
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
    [WorkerApiKeySecurity]
    public async Task<IActionResult> ClaimAsync([FromBody] ClaimJobRequest request, CancellationToken ct)
    {
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

        SliceJob? job = await _jobRepository.ClaimNextJobAsync(
            worker.Id,
            request.Capabilities,
            request.LeaseDurationSeconds,
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
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/progress")]
    [WorkerApiKeySecurity]
    public async Task<IActionResult> ReportProgressAsync(Guid id, [FromBody] SliceJobProgressUpdateRequest request, CancellationToken ct)
    {
        Worker? worker = await GetAuthorizedWorkerAsync();
        if (worker is null)
        {
            return SlicerApiProblems.AuthenticationRequired(this);
        }

        string progressMessage = GetPublicProgressMessage(request.ProgressPercent);
        bool updated = await _jobRepository.TryUpdateProgressForActiveLeaseAsync(
            id,
            worker.Id,
            request.ProgressPercent,
            progressMessage,
            ct);
        if (!updated)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is not null)
        {
            await _eventService.NotifyJobProgressAsync(job, ct);
        }

        return NoContent();
    }

    /// <summary>
    /// Worker marks a job as completed.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="request">Completion details.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/complete")]
    [WorkerApiKeySecurity]
    public async Task<IActionResult> CompleteAsync(Guid id, [FromBody] CompleteSliceJobRequest request, CancellationToken ct)
    {
        Worker? worker = await GetAuthorizedWorkerAsync();
        if (worker is null)
        {
            return SlicerApiProblems.AuthenticationRequired(this);
        }

        SliceJob? job = await _jobRepository.GetByActiveWorkerLeaseAsync(id, worker.Id, ct);
        if (job is null)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

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
            if (artifact is null || artifact.JobId != id || artifact.WorkerId != worker.Id)
            {
                return BadRequest(new { error = "One or more artifacts are invalid for this job." });
            }

            artifacts.Add(artifact);
        }

        string resultFileUrl = $"/api/artifacts/{request.PrimaryArtifactId}";

        bool completed = await _jobRepository.TryCompleteForActiveLeaseAsync(
            id,
            worker.Id,
            resultFileUrl,
            artifactIds,
            request.EstimatedPrintTimeSeconds,
            request.FilamentUsedGrams,
            ct);
        if (!completed)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

        // Re-fetch updated job for event notification
        job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is not null)
        {
            await _eventService.NotifyJobCompletedAsync(job, ct);
        }

        if (_circuitBreaker is not null && job?.WorkerId is { } successWorkerId)
        {
            await _circuitBreaker.RecordJobSuccessAsync(successWorkerId, _workerRepository, ct);
        }

        _metrics.RecordJobCompletion(artifactIds.Count, hasLog: false);

        return Ok(new CompleteSliceJobResponse
        {
            JobId = id,
            Status = SliceJobStatus.Completed,
            CompletedAt = job?.CompletedAt,
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
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/fail")]
    [WorkerApiKeySecurity]
    public async Task<IActionResult> FailAsync(Guid id, [FromBody] FailSliceJobRequest request, CancellationToken ct)
    {
        _ = request;
        Worker? worker = await GetAuthorizedWorkerAsync();
        if (worker is null)
        {
            return SlicerApiProblems.AuthenticationRequired(this);
        }

        bool failed = await _jobRepository.TryFailForActiveLeaseAsync(
            id,
            worker.Id,
            "Slicing worker reported a failure.",
            ct);
        if (!failed)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is not null)
        {
            await _eventService.NotifyJobFailedAsync(job, ct);
        }

        if (_circuitBreaker is not null && job?.WorkerId is { } failWorkerId)
        {
            await _circuitBreaker.RecordJobFailureAsync(failWorkerId, _workerRepository, ct);
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
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/renew-lease")]
    [WorkerApiKeySecurity]
    public async Task<IActionResult> RenewLeaseAsync(Guid id, [FromBody] RenewLeaseRequest request, CancellationToken ct)
    {
        Worker? worker = await GetAuthorizedWorkerAsync();
        if (worker is null)
        {
            return SlicerApiProblems.AuthenticationRequired(this);
        }

        bool renewed = await _jobRepository.RenewLeaseAsync(
            id,
            worker.Id,
            request.LeaseDurationSeconds,
            ct);
        if (renewed)
        {
            return NoContent();
        }

        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is null)
        {
            return NotFound();
        }

        return SlicerApiProblems.ResourceForbidden(this);
    }

    /// <summary>Downloads the model assigned to the authenticated worker for a claimed job.</summary>
    [HttpGet("{id}/model")]
    [WorkerApiKeySecurity]
    public Task<IActionResult> DownloadWorkerModelAsync(Guid id, CancellationToken ct) =>
        DownloadWorkerModelAsync(id, modelIndex: null, ct);

    /// <summary>Downloads one model from a multi-model job assigned to the authenticated worker.</summary>
    [HttpGet("{id}/models/{modelIndex:int}")]
    [WorkerApiKeySecurity]
    public Task<IActionResult> DownloadWorkerModelAsync(Guid id, int modelIndex, CancellationToken ct) =>
        DownloadWorkerModelAsync(id, modelIndex: (int?)modelIndex, ct);

    private async Task<IActionResult> DownloadWorkerModelAsync(
        Guid id,
        int? modelIndex,
        CancellationToken ct)
    {
        Worker? worker = await GetAuthorizedWorkerAsync();
        if (worker is null)
        {
            return SlicerApiProblems.AuthenticationRequired(this);
        }

        SliceJob? job = await _jobRepository.GetByActiveWorkerLeaseAsync(id, worker.Id, ct);
        if (job is null)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

        if (_fileStorage is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

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

        try
        {
            Stream model = await _fileStorage.DownloadFileAsync(modelUrl, ct);
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

    /// <summary>Uploads a G-code artifact for a job owned by the authenticated worker.</summary>
    [HttpPost("{id}/artifacts")]
    [WorkerApiKeySecurity]
    public async Task<IActionResult> UploadWorkerArtifactAsync(
        Guid id,
        IFormFile file,
        CancellationToken ct)
    {
        Worker? worker = await GetAuthorizedWorkerAsync();
        if (worker is null)
        {
            return SlicerApiProblems.AuthenticationRequired(this);
        }

        SliceJob? job = await _jobRepository.GetByActiveWorkerLeaseAsync(id, worker.Id, ct);
        if (job is null)
        {
            return await GetLeaseFenceFailureAsync(id, ct);
        }

        try
        {
            Artifact? artifact = await _artifactsService.UploadForActiveLeaseAsync(
                file,
                id,
                worker.Id,
                "gcode",
                ct);
            if (artifact is null)
            {
                return await GetLeaseFenceFailureAsync(id, ct);
            }

            return Created($"/api/artifacts/{artifact.Id}", new
            {
                id = artifact.Id,
                jobId = artifact.JobId,
                fileName = artifact.FileName,
                contentType = artifact.ContentType,
                sizeBytes = artifact.SizeBytes,
                createdAt = artifact.CreatedAt,
            });
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

        await _jobRepository.RetryJobAsync(id, ct);

        job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is not null)
        {
            await _eventService.NotifyJobQueuedAsync(job, ct);
        }

        return Ok(job is not null ? MapToPublicStatusResponse(job) : null);
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

    private static SliceJobStatusResponse MapToPublicStatusResponse(SliceJob job) => new()
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
        EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
        FilamentUsedGrams = job.FilamentUsedGrams,
        WorkerId = null,
        ModelFileName = SanitizeFileName(job.ModelFileName, "model"),
        SlicerEngine = job.SlicerEngine,
        ArtifactsRoute = $"/api/artifacts/job/{job.Id}",
    };

    private static WorkerSliceJobResponse MapToWorkerResponse(SliceJob job)
    {
        List<string>? modelUrls = DeserializeJsonList<string>(job.ModelFileUrlsJson);
        return new WorkerSliceJobResponse
        {
            Id = job.Id,
            UserId = job.UserId,
            PrinterId = job.PrinterId,
            Status = job.Status,
            ModelFileUrl = $"/api/slice/{job.Id}/model",
            ModelFileName = SanitizeFileName(job.ModelFileName, "model.stl"),
            SlicerEngine = job.SlicerEngine,
            SlicerProfileJson = job.SlicerProfileJson,
            ModelTransformJson = job.ModelTransformJson,
            ModelFileUrls = modelUrls?
                .Select((_, index) => $"/api/slice/{job.Id}/models/{index}")
                .ToList(),
            ModelFileTransforms = DeserializeJsonList<string?>(job.ModelFileTransformsJson),
            RequiredCapabilitiesJson = job.RequiredCapabilitiesJson,
            Priority = job.Priority,
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
}
