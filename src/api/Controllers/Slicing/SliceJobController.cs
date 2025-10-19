using System.Security.Claims;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Repositories.Slicing;
using Farm.Web.Api.Services.Slicing;
using Farm.Web.Shared.Contracts.Slicing;
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
    private readonly ISlicerProfileRepository _profileRepository;

    public SliceJobController(
        ISliceJobRepository jobRepository,
        ISliceJobEventService eventService,
        ILogger<SliceJobController> logger,
        IHostEnvironment env,
        ISlicerProfileRepository profileRepository)
    {
        _jobRepository = jobRepository ?? throw new ArgumentNullException(nameof(jobRepository));
        _eventService = eventService ?? throw new ArgumentNullException(nameof(eventService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _profileRepository = profileRepository ?? throw new ArgumentNullException(nameof(profileRepository));
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
        SlicerProfile? referencedProfile = null;
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
                referencedProfile = await _profileRepository.GetDefaultAsync((Farm.Infrastructure.Domain.SlicerType)request.SlicerEngine, null, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve default slicer profile; continuing with empty profile JSON");
            }
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
            RequiredCapabilitiesJson = request.RequiredCapabilitiesJson ?? "[]",
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
            WorkerId = job.WorkerId
        };

        return Ok(response);
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
            WorkerId = job.WorkerId
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
            WorkerId = job.WorkerId
        }).ToList();

        return Ok(response);
    }
}
