using Farm.Api.Services.Interfaces;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services;
using Farm.Web.Api.DTOs.PrintQueue;
using Farm.Web.Api.DTOs.Retries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// REST API endpoints for managing job retry logic
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RetriesController(
    IRetryService retryService,
    IPrintQueueService printQueueService,
    ILogger<RetriesController> logger) : ControllerBase
{
    private readonly IRetryService _retryService = retryService ?? throw new ArgumentNullException(nameof(retryService));
    private readonly IPrintQueueService _printQueueService = printQueueService ?? throw new ArgumentNullException(nameof(printQueueService));
    private readonly ILogger<RetriesController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Get the current retry policy configuration
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <response code="200">Returns the retry policy</response>
    /// <response code="500">Server error</response>
    [HttpGet("policy")]
    [ProducesResponseType(typeof(RetryPolicyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetRetryPolicyAsync(CancellationToken cancellationToken)
    {
        try
        {
            RetryPolicy policy = await _retryService.GetRetryPolicyAsync(cancellationToken);
            return Ok(new RetryPolicyDto
            {
                Id = policy.Id,
                IsEnabled = policy.IsEnabled,
                MaxRetries = policy.MaxRetries,
                InitialDelaySeconds = policy.InitialDelaySeconds,
                ExponentialBase = policy.ExponentialBase,
                MaxDelaySeconds = policy.MaxDelaySeconds,
                RetryOnErrorCategories = policy.RetryOnErrorCategories
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving retry policy");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve retry policy");
        }
    }

    /// <summary>
    /// Update the retry policy configuration
    /// </summary>
    /// <param name="request">The updated retry policy configuration.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <response code="200">Policy updated successfully</response>
    /// <response code="400">Invalid policy configuration</response>
    /// <response code="401">Unauthorized</response>
    /// <response code="500">Server error</response>
    [HttpPut("policy")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(RetryPolicyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateRetryPolicyAsync(
        [FromBody] UpdateRetryPolicyRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Validation
            if (request.MaxRetries < 0 || request.MaxRetries > 10)
            {
                return BadRequest("MaxRetries must be between 0 and 10");
            }

            if (request.InitialDelaySeconds < 1 || request.InitialDelaySeconds > 3600)
            {
                return BadRequest("InitialDelaySeconds must be between 1 and 3600");
            }

            if (request.ExponentialBase < 1.0 || request.ExponentialBase > 5.0)
            {
                return BadRequest("ExponentialBase must be between 1.0 and 5.0");
            }

            if (request.MaxDelaySeconds < request.InitialDelaySeconds)
            {
                return BadRequest("MaxDelaySeconds must be >= InitialDelaySeconds");
            }

            RetryPolicy currentPolicy = await _retryService.GetRetryPolicyAsync(cancellationToken);

            currentPolicy.IsEnabled = request.IsEnabled;
            currentPolicy.MaxRetries = request.MaxRetries;
            currentPolicy.InitialDelaySeconds = request.InitialDelaySeconds;
            currentPolicy.ExponentialBase = request.ExponentialBase;
            currentPolicy.MaxDelaySeconds = request.MaxDelaySeconds;
            currentPolicy.RetryOnErrorCategories = request.RetryOnErrorCategories;

            RetryPolicy updated = await _retryService.UpdateRetryPolicyAsync(currentPolicy, cancellationToken);

            return Ok(new RetryPolicyDto
            {
                Id = updated.Id,
                IsEnabled = updated.IsEnabled,
                MaxRetries = updated.MaxRetries,
                InitialDelaySeconds = updated.InitialDelaySeconds,
                ExponentialBase = updated.ExponentialBase,
                MaxDelaySeconds = updated.MaxDelaySeconds,
                RetryOnErrorCategories = updated.RetryOnErrorCategories
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating retry policy");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to update retry policy");
        }
    }

    /// <summary>
    /// Get retry history for a specific job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <response code="200">Returns retry history</response>
    /// <response code="404">Job not found</response>
    /// <response code="500">Server error</response>
    [HttpGet("jobs/{jobId}")]
    [ProducesResponseType(typeof(JobRetryDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetJobRetryHistoryAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Verify job exists
            QueuedPrintJobDto? job = await _printQueueService.GetJobByIdAsync(jobId.ToString(), cancellationToken);
            if (job is null)
            {
                return NotFound($"Job {jobId} not found");
            }

            IEnumerable<JobRetry> retries = await _retryService.GetRetryHistoryAsync(jobId, cancellationToken);
            var dtos = retries.Select(r => MapToDto(r)).ToList();

            return Ok(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving retry history for job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve retry history");
        }
    }

    /// <summary>
    /// Get details of a specific retry attempt
    /// </summary>
    /// <param name="retryId">The unique identifier of the retry attempt.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <response code="200">Returns retry details</response>
    /// <response code="404">Retry not found</response>
    /// <response code="500">Server error</response>
    [HttpGet("{retryId}")]
    [ProducesResponseType(typeof(JobRetryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetRetryAsync(
        Guid retryId,
        CancellationToken cancellationToken)
    {
        try
        {
            JobRetry? retry = await _retryService.GetRetryAsync(retryId, cancellationToken);
            return retry is null ? NotFound($"Retry {retryId} not found") : Ok(MapToDto(retry));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving retry {RetryId}", retryId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve retry");
        }
    }

    /// <summary>
    /// Get all pending retries that are due to execute
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <response code="200">Returns list of due retries</response>
    /// <response code="500">Server error</response>
    [HttpGet("due/list")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(JobRetryDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetDueRetriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            IEnumerable<JobRetry> retries = await _retryService.GetDueRetriesAsync(cancellationToken);
            var dtos = retries.Select(r => MapToDto(r)).ToList();

            return Ok(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving due retries");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to retrieve due retries");
        }
    }

    /// <summary>
    /// Check if a job should be automatically retried
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to check.</param>
    /// <param name="request">The retry check request containing error category.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <response code="200">Returns whether job should be retried</response>
    /// <response code="400">Invalid error category</response>
    /// <response code="404">Job not found</response>
    /// <response code="500">Server error</response>
    [HttpPost("jobs/{jobId}/check-retry")]
    [Authorize(Roles = "Admin")]
    [ProducesResponseType(typeof(CheckRetryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CheckShouldRetryAsync(
        Guid jobId,
        [FromBody] CheckRetryRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Verify job exists
            QueuedPrintJobDto? job = await _printQueueService.GetJobByIdAsync(jobId.ToString(), cancellationToken);
            if (job is null)
            {
                return NotFound($"Job {jobId} not found");
            }

            // Validate error category
            if (!Enum.TryParse<ErrorCategory>(request.ErrorCategory, out ErrorCategory category))
            {
                return BadRequest($"Invalid error category: {request.ErrorCategory}");
            }

            bool shouldRetry = await _retryService.ShouldRetryAsync(jobId, category, cancellationToken);

            return Ok(new CheckRetryResponse { ShouldRetry = shouldRetry });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking retry eligibility for job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to check retry eligibility");
        }
    }

    private static JobRetryDto MapToDto(JobRetry retry)
    {
        return new JobRetryDto
        {
            Id = retry.Id,
            OriginalJobId = retry.OriginalJobId,
            RetryJobId = retry.RetryJobId,
            AttemptNumber = retry.AttemptNumber,
            ErrorCategory = retry.ErrorCategory.ToString(),
            FailureReason = retry.FailureReason,
            Status = retry.Status,
            ScheduledRetryTime = retry.ScheduledRetryTime,
            ActualRetryTime = retry.ActualRetryTime,
            Notes = retry.Notes,
            CreatedAt = retry.CreatedAt,
            UpdatedAt = retry.UpdatedAt
        };
    }
}
