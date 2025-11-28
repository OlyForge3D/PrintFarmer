using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
// ClaimJobRequest now lives in shared contracts
using Farm.Infrastructure.Contracts.Slicing;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Farm.Infrastructure.Repositories.Workers;
using Farm.Web.Api.Services.Artifacts;
using Farm.Web.Api.Services.RateLimiting;
using Farm.Web.Api.Services.Slicing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers.Slicing;

/// <summary>
/// Controller for managing distributed slicing jobs with capability-aware dispatching
/// </summary>
[ApiController]
[Route("api/slice")]
[Tags("Slice Jobs")]
[Authorize] // All endpoints require authentication
public class SliceJobController : ControllerBase
{
    private readonly ISliceJobRepository _jobRepository;
    private readonly ISliceJobEventService _eventService;
    private readonly ILogger<SliceJobController> _logger;
    private readonly IHostEnvironment _env;
    private readonly IProcessProfileRepository _profileRepository;
    private readonly IArtifactsService _artifactsService;
    private readonly SliceJobMetrics _metrics;
    private readonly IRateLimitService _rateLimitService;
    private readonly Services.Workers.IWorkerAuthService _workerAuth;
    private readonly Services.Workers.IWorkerCircuitBreakerService? _circuitBreaker;

    public SliceJobController(
        ISliceJobRepository jobRepository,
        ISliceJobEventService eventService,
        ILogger<SliceJobController> logger,
        IHostEnvironment env,
        IProcessProfileRepository profileRepository,
        IArtifactsService artifactsService,
        IRateLimitService rateLimitService,
        SliceJobMetrics metrics,
        Services.Workers.IWorkerAuthService workerAuth,
        Services.Workers.IWorkerCircuitBreakerService? circuitBreaker = null)
    {
        _jobRepository = jobRepository ?? throw new ArgumentNullException(nameof(jobRepository));
        _eventService = eventService ?? throw new ArgumentNullException(nameof(eventService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
        _artifactsService = artifactsService ?? throw new ArgumentNullException(nameof(artifactsService));
        _rateLimitService = rateLimitService ?? throw new ArgumentNullException(nameof(rateLimitService));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _workerAuth = workerAuth ?? throw new ArgumentNullException(nameof(workerAuth));
        _circuitBreaker = circuitBreaker;
    }

    /// <summary>
    /// Validates capability JSON string. Ensures JSON array; size &lt;= 32; distinct; simple lowercase slugs.
    /// Returns sanitized canonical list (lower-case) or error message.
    /// </summary>
    public static bool TryValidateCapabilities(string? capabilitiesJson, out string[] capabilities, out string? error)
    {
        capabilities = Array.Empty<string>();
        error = null;
        if (string.IsNullOrWhiteSpace(capabilitiesJson))
        {
            return true; // empty allowed
        }
        try
        {
            string[]? parsed = JsonSerializer.Deserialize<string[]>(capabilitiesJson);
            if (parsed == null)
            {
                error = "Capabilities must be a JSON string array.";
                return false;
            }
            if (parsed.Length > 32)
            {
                error = "Too many capabilities (max 32).";
                return false;
            }
            string[] canonical = parsed
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim().ToLowerInvariant())
                .ToArray();
            if (canonical.Length != canonical.Distinct(StringComparer.Ordinal).Count())
            {
                error = "Duplicate capabilities are not allowed.";
                return false;
            }
            List<string> invalid = canonical.Where(c => !Regex.IsMatch(c, @"^[a-z0-9][a-z0-9\-_/]{0,63}$")).ToList();
            if (invalid.Count > 0)
            {
                error = $"Invalid capability slug(s): {string.Join(", ", invalid)}";
                return false;
            }
            capabilities = canonical;
            return true;
        }
        catch (System.Text.Json.JsonException ex)
        {
            error = $"Invalid capabilities JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Submit a new slicing job to the distributed queue
    /// </summary>
    /// <param name="request">Job submission details</param>
    /// <returns>Job ID and queue position</returns>
    [HttpPost]
    [ProducesResponseType(typeof(SubmitSliceJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SubmitJobAsync([FromBody] SubmitSliceJobRequest request)
    {
        if (request == null)
        {
            return BadRequest("Request body is required");
        }

        // Determine authenticated user if not provided
        Guid userId = request.UserId;
        if (userId == Guid.Empty)
        {
            Claim? subClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
            if (subClaim == null || !Guid.TryParse(subClaim.Value, out userId) || userId == Guid.Empty)
            {
                if (_env.IsEnvironment("Testing"))
                {
                    // Provide a stable test user id for integration tests
                    userId = Guid.Parse("00000000-0000-0000-0000-000000000001");
                }
                else
                {
                    return Unauthorized("Authenticated user is required to submit slicing jobs");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(request.ModelFileUrl))
        {
            return BadRequest("Model file URL is required");
        }

        if (string.IsNullOrWhiteSpace(request.ModelFileName))
        {
            return BadRequest("Model file name is required");
        }

        // Resolve profile if provided (profile takes precedence over raw JSON string)
        ProcessProfile? referencedProfile = null;
        if (request.SlicerProfileId.HasValue)
        {
            referencedProfile = await _profileRepository.GetByIdAsync(request.SlicerProfileId.Value, HttpContext.RequestAborted);
            if (referencedProfile == null)
            {
                return BadRequest($"Slicer profile {request.SlicerProfileId.Value} not found");
            }
        }

        // If no explicit profile supplied but JSON absent, attempt default profile for engine
        if (referencedProfile == null && string.IsNullOrWhiteSpace(request.SlicerProfileJson))
        {
            try
            {
                referencedProfile = await _profileRepository.GetDefaultAsync((SlicerType)request.SlicerEngine, null, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve default slicer profile; continuing with empty profile JSON");
            }
        }

        // Rate limit per authenticated user (or test user fallback)
        RateLimitResult rateResult = await _rateLimitService.CheckSliceJobSubmitLimitAsync(userId, HttpContext.RequestAborted);
        if (!rateResult.IsAllowed)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, rateResult.Message ?? "Too many slice job submissions. Please retry later.");
        }

        // Capabilities validation (optional requirement)
        if (!TryValidateCapabilities(request.RequiredCapabilitiesJson, out string[]? capabilityList, out string? capabilityError))
        {
            return BadRequest(capabilityError);
        }

        // Create new job
        SliceJob job = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PrinterId = request.PrinterId,
            ModelFileUrl = request.ModelFileUrl,
            ModelFileName = request.ModelFileName,
            SlicerEngine = referencedProfile != null ? (int)referencedProfile.SlicerType : request.SlicerEngine,
            SlicerProfileJson = referencedProfile?.RawJson ?? request.SlicerProfileJson ?? "{}",
            SlicerProfileId = referencedProfile?.Id,
            RequiredCapabilitiesJson = capabilityList.Length == 0 ? "[]" : JsonSerializer.Serialize(capabilityList),
            Status = SliceJobStatus.Queued,
            Priority = request.Priority,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _jobRepository.AddAsync(job);
        await _jobRepository.SaveChangesAsync();

        // Broadcast job queued event
        await _eventService.NotifyJobQueuedAsync(job, HttpContext.RequestAborted);

        // Calculate queue position
        IReadOnlyList<SliceJob> queuedJobs = await _jobRepository.GetQueuedJobsAsync(1000);
        int queuePosition = queuedJobs.Select((j, i) => new { j.Id, Index = i }).FirstOrDefault(x => x.Id == job.Id)?.Index + 1 ?? -1;

        _logger.LogInformation("Job {JobId} submitted by user {UserId} for printer {PrinterId}",
            job.Id, job.UserId, job.PrinterId ?? Guid.Empty);

        SubmitSliceJobResponse response = new()
        {
            JobId = job.Id,
            Status = job.Status,
            QueuedAt = job.QueuedAt,
            QueuePosition = queuePosition
        };

        await _rateLimitService.RecordSliceJobSubmitAttemptAsync(userId, HttpContext.RequestAborted);
        return Accepted(response);
    }

    /// <summary>
    /// Get the current status of a slicing job
    /// </summary>
    /// <param name="id">Job ID</param>
    /// <returns>Job status details</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(SliceJobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobStatusAsync(Guid id)
    {
        SliceJob? job = await _jobRepository.GetByIdAsync(id);
        if (job == null)
        {
            return NotFound($"Job {id} not found");
        }

        SliceJobStatusResponse response = new()
        {
            Id = job.Id,
            Status = job.Status,
            ProgressPercent = job.ProgressPercent,
            ProgressMessage = job.ProgressMessage,
            QueuedAt = job.QueuedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            ResultFileUrl = job.ResultFileUrl,
            ErrorMessage = job.ErrorMessage,
            EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
            FilamentUsedGrams = job.FilamentUsedGrams,
            WorkerId = job.WorkerId,
            ModelFileUrl = job.ModelFileUrl,
            ModelFileName = job.ModelFileName,
            SlicerEngine = job.SlicerEngine,
            SlicerProfileJson = job.SlicerProfileJson
        };

        return Ok(response);
    }

    /// <summary>
    /// Update progress for a processing job (worker endpoint). Emits SignalR JobProgress event.
    /// </summary>
    /// <param name="id">Job ID</param>
    /// <param name="request">Progress payload</param>
    /// <returns>No content on success</returns>
    [HttpPost("{id}/progress")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateProgressAsync(Guid id, [FromBody] SliceJobProgressUpdateRequest request)
    {
        if (!_workerAuth.IsAuthorized(HttpContext))
        {
            return Unauthorized("Worker API key missing or invalid");
        }
        if (request == null)
        {
            return BadRequest("Request body required");
        }
        if (request.ProgressPercent < 0 || request.ProgressPercent > 100)
        {
            return BadRequest("ProgressPercent must be between 0 and 100");
        }
        SliceJob? job = await _jobRepository.GetByIdAsync(id, HttpContext.RequestAborted);
        if (job == null)
        {
            return NotFound($"Job {id} not found");
        }
        if (job.Status != SliceJobStatus.Processing)
        {
            return BadRequest($"Cannot update progress for job in status {job.Status}");
        }

        // Update repository state
        await _jobRepository.UpdateProgressAsync(id, request.ProgressPercent, request.ProgressMessage ?? string.Empty, HttpContext.RequestAborted);
        await _jobRepository.SaveChangesAsync(HttpContext.RequestAborted);

        // Reload for broadcasting enriched event
        SliceJob? updated = await _jobRepository.GetByIdAsync(id, HttpContext.RequestAborted);
        if (updated != null)
        {
            await _eventService.NotifyJobProgressAsync(updated, HttpContext.RequestAborted);
        }
        return NoContent();
    }

    /// <summary>
    /// Cancel a queued or processing slicing job
    /// </summary>
    /// <param name="id">Job ID to cancel</param>
    /// <returns>No content on success</returns>
    [HttpPost("{id}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CancelJobAsync(Guid id)
    {
        SliceJob? job = await _jobRepository.GetByIdAsync(id);
        if (job == null)
        {
            return NotFound($"Job {id} not found");
        }

        // Only allow cancellation of queued or processing jobs
        if (job.Status != SliceJobStatus.Queued && job.Status != SliceJobStatus.Processing)
        {
            return BadRequest($"Cannot cancel job in status {job.Status}. Only Queued or Processing jobs can be cancelled.");
        }

        await _jobRepository.UpdateStatusAsync(id, SliceJobStatus.Cancelled);
        await _jobRepository.SaveChangesAsync();

        // Broadcast job cancelled event
        await _eventService.NotifyJobCancelledAsync(job, HttpContext.RequestAborted);

        _logger.LogInformation("Job {JobId} cancelled by user request", id);

        return NoContent();
    }

    /// <summary>
    /// Renew a job lease to extend LeaseExpiresAt while worker is actively processing
    /// </summary>
    [HttpPost("{id}/renew")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RenewLeaseAsync(Guid id, [FromBody] RenewLeaseRequest request)
    {
        if (!_workerAuth.IsAuthorized(HttpContext))
        {
            return Unauthorized("Worker API key missing or invalid");
        }

        SliceJob? job = await _jobRepository.GetByIdAsync(id, HttpContext.RequestAborted);
        if (job == null)
        {
            return NotFound($"Job {id} not found");
        }

        // Only allow renewing leases for Processing jobs
        if (job.Status != SliceJobStatus.Processing)
        {
            return BadRequest($"Cannot renew lease for job in status {job.Status}");
        }

        int duration = request?.LeaseDurationSeconds ?? 300; // default to 300 if not specified
        await _jobRepository.RenewLeaseAsync(id, duration, HttpContext.RequestAborted);
        await _jobRepository.SaveChangesAsync(HttpContext.RequestAborted);

        return NoContent();
    }

    /// <summary>
    /// Get all jobs for the authenticated user
    /// </summary>
    /// <param name="limit">Maximum number of jobs to return</param>
    /// <param name="offset">Number of jobs to skip</param>
    /// <returns>List of user's jobs</returns>
    [HttpGet("my-jobs")]
    [ProducesResponseType(typeof(List<SliceJobStatusResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyJobsAsync([FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        // Determine authenticated user
        Claim? subClaim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        if (subClaim == null || !Guid.TryParse(subClaim.Value, out Guid userId) || userId == Guid.Empty)
        {
            if (_env.IsEnvironment("Testing"))
            {
                userId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            }
            else
            {
                return Unauthorized("Authenticated user is required");
            }
        }

        IReadOnlyList<SliceJob> jobs = await _jobRepository.GetByUserIdAsync(userId, limit, offset);

        List<SliceJobStatusResponse> response = jobs.Select(job => new SliceJobStatusResponse
        {
            Id = job.Id,
            Status = job.Status,
            ProgressPercent = job.ProgressPercent,
            ProgressMessage = job.ProgressMessage,
            QueuedAt = job.QueuedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            ResultFileUrl = job.ResultFileUrl,
            ErrorMessage = job.ErrorMessage,
            EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
            FilamentUsedGrams = job.FilamentUsedGrams,
            WorkerId = job.WorkerId,
            ModelFileUrl = job.ModelFileUrl,
            ModelFileName = job.ModelFileName,
            SlicerEngine = job.SlicerEngine,
            SlicerProfileJson = job.SlicerProfileJson
        }).ToList();

        return Ok(response);
    }

    /// <summary>
    /// Get all jobs in the queue (admin endpoint)
    /// </summary>
    /// <param name="limit">Maximum number of jobs to return</param>
    /// <returns>List of queued jobs</returns>
    [HttpGet("queue")]
    [ProducesResponseType(typeof(List<SliceJobStatusResponse>), StatusCodes.Status200OK)]
    [Authorize(Policy = "CanViewSliceQueue")] // Restrict queue visibility via policy
    public async Task<IActionResult> GetQueueAsync([FromQuery] int limit = 100)
    {
        IReadOnlyList<SliceJob> jobs = await _jobRepository.GetQueuedJobsAsync(limit);

        List<SliceJobStatusResponse> response = jobs.Select(job => new SliceJobStatusResponse
        {
            Id = job.Id,
            Status = job.Status,
            ProgressPercent = job.ProgressPercent,
            ProgressMessage = job.ProgressMessage,
            QueuedAt = job.QueuedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            ResultFileUrl = job.ResultFileUrl,
            ErrorMessage = job.ErrorMessage,
            EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
            FilamentUsedGrams = job.FilamentUsedGrams,
            WorkerId = job.WorkerId,
            ModelFileUrl = job.ModelFileUrl,
            ModelFileName = job.ModelFileName,
            SlicerEngine = job.SlicerEngine,
            SlicerProfileJson = job.SlicerProfileJson
        }).ToList();

        return Ok(response);
    }

    /// <summary>
    /// Claim the next available job from the queue (worker pull model)
    /// </summary>
    /// <param name="request">Claim request with worker ID and capabilities</param>
    /// <returns>Claimed job details or 204 if no jobs available</returns>
    [HttpPost("claim")]
    [ProducesResponseType(typeof(SliceJobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ClaimJobAsync([FromBody] ClaimJobRequest request)
    {
        if (!_workerAuth.IsAuthorized(HttpContext))
        {
            return Unauthorized("Worker API key missing or invalid");
        }
        try
        {
            if (request.LeaseDurationSeconds < 30 || request.LeaseDurationSeconds > 3600)
            {
                return BadRequest("Lease duration must be between 30 and 3600 seconds");
            }

            SliceJob? job = await _jobRepository.ClaimNextJobAsync(
                request.WorkerId,
                request.Capabilities,
                request.LeaseDurationSeconds,
                HttpContext.RequestAborted);

            if (job == null)
            {
                return NoContent(); // No jobs available
            }

            // Broadcast job started event (do not fail claim if broadcast throws)
            try
            {
                await _eventService.NotifyJobStartedAsync(job, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Claim succeeded but event broadcast failed for job {JobId}", job.Id);
            }

            _logger.LogInformation("Job {JobId} claimed by worker {WorkerId} with lease until {LeaseExpires}",
                job.Id, request.WorkerId, job.LeaseExpiresAt);

            SliceJobStatusResponse response = new()
            {
                Id = job.Id,
                Status = job.Status,
                ProgressPercent = job.ProgressPercent,
                ProgressMessage = job.ProgressMessage,
                QueuedAt = job.QueuedAt,
                StartedAt = job.StartedAt,
                CompletedAt = job.CompletedAt,
                ResultFileUrl = job.ResultFileUrl,
                ErrorMessage = job.ErrorMessage,
                EstimatedPrintTimeSeconds = job.EstimatedPrintTimeSeconds,
                FilamentUsedGrams = job.FilamentUsedGrams,
                WorkerId = job.WorkerId,
                ModelFileUrl = job.ModelFileUrl,
                ModelFileName = job.ModelFileName,
                SlicerEngine = job.SlicerEngine,
                SlicerProfileJson = job.SlicerProfileJson
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            // Surface diagnostic details during Testing environment only
            if (_env.IsEnvironment("Testing"))
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Claim failure: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            _logger.LogError(ex, "Unhandled exception in ClaimJobAsync");
            return StatusCode(StatusCodes.Status500InternalServerError, "Claim failed");
        }
    }

    /// <summary>
    /// Mark a processing slice job as completed and associate produced artifacts.
    /// </summary>
    /// <param name="id">Slice job identifier</param>
    /// <param name="request">Completion details (primary artifact + optional additional artifact IDs)</param>
    [HttpPost("{id}/complete")]
    [ProducesResponseType(typeof(CompleteSliceJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CompleteJobAsync(Guid id, [FromBody] CompleteSliceJobRequest request)
    {
        if (!_workerAuth.IsAuthorized(HttpContext))
        {
            return Unauthorized("Worker API key missing or invalid");
        }
        if (request == null)
        {
            return BadRequest("Request body required");
        }

        if (!_workerAuth.IsAuthorized(HttpContext))
        {
            return Unauthorized("Worker API key missing or invalid");
        }
        SliceJob? job = await _jobRepository.GetByIdAsync(id);
        if (job == null)
        {
            return NotFound($"Job {id} not found");
        }

        if (job.Status != SliceJobStatus.Processing)
        {
            return BadRequest($"Cannot complete job in status {job.Status}. Must be Processing.");
        }

        // Fetch primary artifact
        Artifact? primary = await _artifactsService.GetAsync(request.PrimaryArtifactId, HttpContext.RequestAborted);
        if (primary == null)
        {
            return BadRequest($"Primary artifact {request.PrimaryArtifactId} not found");
        }
        if (primary.JobId != job.Id)
        {
            return BadRequest("Primary artifact job mismatch");
        }

        // Validate additional artifacts if provided
        List<Guid> allArtifactIds = new List<Guid> { primary.Id };
        Guid? logArtifactId = null;
        if (request.AdditionalArtifactIds != null)
        {
            foreach (Guid aid in request.AdditionalArtifactIds.Distinct())
            {
                Artifact? extra = await _artifactsService.GetAsync(aid, HttpContext.RequestAborted);
                if (extra == null)
                {
                    return BadRequest($"Artifact {aid} not found");
                }
                if (extra.JobId != job.Id)
                {
                    return BadRequest($"Artifact {aid} does not belong to job {job.Id}");
                }
                allArtifactIds.Add(aid);
            }
        }

        // If inline log provided and no explicit log artifact already referenced, persist it
        if (!string.IsNullOrWhiteSpace(request.LogText))
        {
            bool hasLogArtifactReferenced = request.AdditionalArtifactIds?.Any(aid => aid == primary.Id) == true; // simplistic; actual log detection can be added later
            if (!hasLogArtifactReferenced)
            {
                Artifact logArtifact = await _artifactsService.UploadTextAsync(request.LogText, "slicer-log.txt", job.Id, job.WorkerId, "log", HttpContext.RequestAborted);
                allArtifactIds.Add(logArtifact.Id);
                logArtifactId = logArtifact.Id;
            }
        }

        // Derive stable URL from stored relative path (primary artifact governs result URL)
        string resultUrl = $"/api/artifacts/{primary.Id}/download";

        await _jobRepository.MarkCompletedWithArtifactsAsync(
            job.Id,
            resultUrl,
            allArtifactIds,
            request.EstimatedPrintTimeSeconds,
            request.FilamentUsedGrams,
            HttpContext.RequestAborted);
        await _jobRepository.SaveChangesAsync(HttpContext.RequestAborted);

        // Record successful completion in circuit breaker
        if (job.WorkerId.HasValue && job.WorkerId.Value != Guid.Empty && _circuitBreaker != null)
        {
            IWorkerRepository workerRepo = HttpContext.RequestServices.GetRequiredService<IWorkerRepository>();
            await _circuitBreaker.RecordJobSuccessAsync(job.WorkerId.Value, workerRepo, HttpContext.RequestAborted);
        }

        // Reload updated job for broadcasting
        SliceJob? updated = await _jobRepository.GetByIdAsync(job.Id, HttpContext.RequestAborted);
        if (updated != null)
        {
            await _eventService.NotifyJobCompletedAsync(updated, HttpContext.RequestAborted);
        }

        CompleteSliceJobResponse response = new CompleteSliceJobResponse
        {
            JobId = job.Id,
            Status = SliceJobStatus.Completed,
            CompletedAt = updated?.CompletedAt,
            ResultFileUrl = resultUrl,
            ArtifactIds = allArtifactIds.ToArray(),
            EstimatedPrintTimeSeconds = request.EstimatedPrintTimeSeconds,
            FilamentUsedGrams = request.FilamentUsedGrams,
            LogArtifactId = logArtifactId
        };
        if (updated != null)
        {
            response.ArtifactsCount = updated.ArtifactsCount;
            response.ArtifactsTotalBytes = updated.ArtifactsTotalBytes;
        }

        _logger.LogInformation("Job {JobId} completed with {ArtifactCount} artifacts", job.Id, allArtifactIds.Count);

        // Record completion metrics
        bool hasLog = logArtifactId.HasValue || (request.AdditionalArtifactIds?.Any(aid =>
        {
            Artifact? a = _artifactsService.GetAsync(aid, HttpContext.RequestAborted).Result;
            return a?.Kind == "log";
        }) ?? false);
        _metrics.RecordJobCompletion(allArtifactIds.Count, hasLog);

        return Ok(response);
    }
}
