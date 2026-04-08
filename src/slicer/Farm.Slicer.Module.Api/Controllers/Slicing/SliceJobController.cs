using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Farm.Infrastructure;
using Farm.Slicer.Module.Contracts;
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
public class SliceJobController(
    ISliceJobRepository jobRepository,
    ISliceJobEventService eventService,
    ILogger<SliceJobController> logger,
    IArtifactsService artifactsService,
    IRateLimitService rateLimitService,
    SliceJobMetrics metrics,
    IWorkerAuthService workerAuth,
    IWorkerRepository workerRepository,
    IOptions<Farm.Slicer.Module.Settings.SlicerSettings> slicerOptions,
    IWorkerCircuitBreakerService? circuitBreaker = null) : ControllerBase
{
    private readonly ISliceJobRepository _jobRepository = jobRepository;
    private readonly ISliceJobEventService _eventService = eventService;
    private readonly ILogger<SliceJobController> _logger = logger;
    private readonly IArtifactsService _artifactsService = artifactsService;
    private readonly IRateLimitService _rateLimitService = rateLimitService;
    private readonly SliceJobMetrics _metrics = metrics;
    private readonly IWorkerAuthService _workerAuth = workerAuth;
    private readonly IWorkerRepository _workerRepository = workerRepository;
    private readonly Farm.Slicer.Module.Settings.SlicerSettings _slicerSettings = slicerOptions.Value;
    private readonly IWorkerCircuitBreakerService? _circuitBreaker = circuitBreaker;

    /// <summary>
    /// Submits a new slice job.
    /// </summary>
    /// <param name="request">The submission request.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    [Authorize]
    public async Task<IActionResult> SubmitAsync([FromBody] SubmitSliceJobRequest request, CancellationToken ct)
    {
        string userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

        // Rate limiting
        SlicerRateLimitResult rateLimitResult = await _rateLimitService.CheckAsync($"slice-job:{userId}", ct);
        if (!rateLimitResult.IsAllowed)
        {
            return StatusCode(429, new { error = "Rate limit exceeded.", retryAfterSeconds = rateLimitResult.RetryAfterSeconds });
        }

        var job = new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.TryParse(userId, out Guid uid) ? uid : Guid.Empty,
            PrinterId = request.PrinterId,
            ModelFileUrl = request.ModelFileUrl,
            ModelFileName = request.ModelFileName,
            SlicerEngine = request.SlicerEngine,
            SlicerProfileJson = request.SlicerProfileJson,
            SlicerProfileId = request.SlicerProfileId,
            RequiredCapabilitiesJson = request.RequiredCapabilitiesJson,
            Priority = request.Priority,
            Status = SliceJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await _jobRepository.AddAsync(job, ct);
        await _eventService.NotifyJobQueuedAsync(job, ct);

        return Created($"/api/slice/{job.Id}", new SubmitSliceJobResponse
        {
            JobId = job.Id,
            Status = job.Status,
        });
    }

    /// <summary>
    /// Gets a slice job by ID.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetAsync(Guid id, CancellationToken ct)
    {
        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is null)
        {
            return NotFound();
        }

        return Ok(MapToStatusResponse(job));
    }

    /// <summary>
    /// Gets the current user's slice jobs.
    /// </summary>
    /// <param name="limit">Maximum number of jobs to return (default 100).</param>
    /// <param name="offset">Number of jobs to skip (default 0).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("my-jobs")]
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
        return Ok(jobs.Select(MapToStatusResponse).ToList());
    }

    /// <summary>
    /// Lists slice jobs with pagination and optional filtering.
    /// </summary>
    /// <param name="page">Page number (1-based, default 1).</param>
    /// <param name="pageSize">Items per page (default 20, max 100).</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="sortBy">Sort field: CreatedAt (default) or CompletedAt.</param>
    /// <param name="sortDir">Sort direction: asc or desc (default desc).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = "desc",
        CancellationToken ct = default)
    {
        if (page < 1)
        {
            page = 1;
        }

        pageSize = Math.Clamp(pageSize, 1, 100);

        int totalCount = await _jobRepository.CountAsync(status, ct);
        IReadOnlyList<SliceJob> jobs = await _jobRepository.GetPagedAsync(page, pageSize, status, sortBy, sortDir, ct);
        int totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling((double)totalCount / pageSize);

        return Ok(new PagedResult<SliceJobStatusResponse>(
            jobs.Select(MapToStatusResponse).ToList(),
            totalCount,
            page,
            pageSize,
            totalPages));
    }

    /// <summary>
    /// Worker claims the next available job.
    /// </summary>
    /// <param name="request">Claim request with worker details.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("claim")]
    public async Task<IActionResult> ClaimAsync([FromBody] ClaimJobRequest request, CancellationToken ct)
    {
        if (!_workerAuth.IsAuthorized(HttpContext))
        {
            return Unauthorized(new { error = "Invalid worker credentials." });
        }

        // Check circuit breaker
        if (_circuitBreaker is not null)
        {
            WorkerCircuitState state = _circuitBreaker.GetCircuitState(request.WorkerId);
            if (state == WorkerCircuitState.Open)
            {
                return StatusCode(503, new { error = "Circuit breaker is open for this worker.", state = state.ToString() });
            }
        }

        SliceJob? job = await _jobRepository.ClaimNextJobAsync(request.WorkerId, request.Capabilities, request.LeaseDurationSeconds, ct);
        if (job is null)
        {
            return NoContent();
        }

        await _eventService.NotifyJobStartedAsync(job, ct);
        _logger.LogInformation("Job {JobId} claimed by worker {WorkerId}", job.Id, request.WorkerId);

        return Ok(MapToStatusResponse(job));
    }

    /// <summary>
    /// Worker reports progress on a claimed job.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="request">Progress update.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/progress")]
    public async Task<IActionResult> ReportProgressAsync(Guid id, [FromBody] SliceJobProgressUpdateRequest request, CancellationToken ct)
    {
        if (!_workerAuth.IsAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is null)
        {
            return NotFound();
        }

        await _jobRepository.UpdateProgressAsync(id, request.ProgressPercent, request.ProgressMessage ?? string.Empty, ct);
        job.ProgressPercent = request.ProgressPercent;
        job.ProgressMessage = request.ProgressMessage;
        await _eventService.NotifyJobProgressAsync(job, ct);

        return NoContent();
    }

    /// <summary>
    /// Worker marks a job as completed.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="request">Completion details.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/complete")]
    public async Task<IActionResult> CompleteAsync(Guid id, [FromBody] CompleteSliceJobRequest request, CancellationToken ct)
    {
        if (!_workerAuth.IsAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is null)
        {
            return NotFound();
        }

        // Upload log text as artifact if provided
        Artifact? logArtifact = null;
        if (!string.IsNullOrWhiteSpace(request.LogText))
        {
            logArtifact = await _artifactsService.UploadTextAsync(
                request.LogText, "slicing.log", id, job.WorkerId, "log", ct);
        }

        // Collect all artifact IDs
        var artifactIds = new List<Guid> { request.PrimaryArtifactId };
        if (request.AdditionalArtifactIds is { Length: > 0 })
        {
            artifactIds.AddRange(request.AdditionalArtifactIds);
        }

        if (logArtifact is not null)
        {
            artifactIds.Add(logArtifact.Id);
        }

        // Resolve result file URL from primary artifact
        Artifact? primary = await _artifactsService.GetAsync(request.PrimaryArtifactId, ct);
        string resultFileUrl = primary?.RelativePath ?? string.Empty;

        await _jobRepository.MarkCompletedWithArtifactsAsync(
            id, resultFileUrl, artifactIds, request.EstimatedPrintTimeSeconds, request.FilamentUsedGrams, ct);

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

        _metrics.RecordJobCompletion(artifactIds.Count, logArtifact is not null);

        return Ok(new CompleteSliceJobResponse
        {
            JobId = id,
            Status = SliceJobStatus.Completed,
            CompletedAt = job?.CompletedAt,
            ResultFileUrl = resultFileUrl,
            ArtifactIds = artifactIds.ToArray(),
            EstimatedPrintTimeSeconds = request.EstimatedPrintTimeSeconds,
            FilamentUsedGrams = request.FilamentUsedGrams,
            LogArtifactId = logArtifact?.Id,
            ArtifactsCount = artifactIds.Count,
        });
    }

    /// <summary>
    /// Worker marks a job as failed.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="request">Failure details.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/fail")]
    public async Task<IActionResult> FailAsync(Guid id, [FromBody] FailSliceJobRequest request, CancellationToken ct)
    {
        if (!_workerAuth.IsAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is null)
        {
            return NotFound();
        }

        await _jobRepository.MarkFailedAsync(id, request.ErrorMessage, ct);

        job = await _jobRepository.GetByIdAsync(id, ct);
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
    public async Task<IActionResult> RenewLeaseAsync(Guid id, [FromBody] RenewLeaseRequest request, CancellationToken ct)
    {
        if (!_workerAuth.IsAuthorized(HttpContext))
        {
            return Unauthorized();
        }

        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is null)
        {
            return NotFound();
        }

        await _jobRepository.RenewLeaseAsync(id, request.LeaseDurationSeconds, ct);

        return NoContent();
    }

    /// <summary>
    /// Cancels a slice job.
    /// </summary>
    /// <param name="id">The job ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id}/cancel")]
    [Authorize]
    public async Task<IActionResult> CancelAsync(Guid id, CancellationToken ct)
    {
        SliceJob? job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is null)
        {
            return NotFound();
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

        if (job.Status is not SliceJobStatus.Failed)
        {
            return BadRequest(new { error = $"Only failed jobs can be retried. Current status: {job.Status}" });
        }

        await _jobRepository.RetryJobAsync(id, ct);

        job = await _jobRepository.GetByIdAsync(id, ct);
        if (job is not null)
        {
            await _eventService.NotifyJobQueuedAsync(job, ct);
        }

        return Ok(job is not null ? MapToStatusResponse(job) : null);
    }

    /// <summary>
    /// Gets worker circuit breaker states.
    /// </summary>
    [HttpGet("circuit-breakers")]
    [Authorize]
    public IActionResult GetCircuitBreakerStates()
    {
        if (_circuitBreaker is null)
        {
            return Ok(new { enabled = false });
        }

        _circuitBreaker.CheckCircuits();
        return Ok(new { enabled = true });
    }

    private static SliceJobStatusResponse MapToStatusResponse(SliceJob job) => new()
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
        SlicerProfileJson = job.SlicerProfileJson,
    };
}

/// <summary>Request body for reporting a failed slice job.</summary>
public record FailSliceJobRequest(string ErrorMessage);
