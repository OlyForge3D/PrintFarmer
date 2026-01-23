using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for job completion time predictions (Phase 4.2)
/// Provides endpoints for getting predicted completion times and statistics
/// </summary>
[ApiController]
[Route("api/predictions")]
[Authorize]
[Produces("application/json")]
public class PredictionController(PredictionService predictionService) : ControllerBase
{
    /// <summary>
    /// Get predicted completion time for a job
    /// </summary>
    /// <param name="jobId">The job ID to predict for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Prediction with estimated completion time and confidence level</returns>
    [HttpGet("jobs/{jobId:guid}/completion")]
    [ProducesResponseType(typeof(CompletionPredictionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CompletionPredictionDto>> GetCompletionPredictionAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            CompletionPredictionDto prediction = await predictionService.PredictCompletionTimeByJobIdAsync(jobId, cancellationToken);
            return Ok(prediction);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get recorded statistics for a completed job
    /// </summary>
    /// <param name="jobId">The job ID to get statistics for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Job statistics if available</returns>
    [HttpGet("jobs/{jobId:guid}/statistics")]
    [ProducesResponseType(typeof(PrintJobStatisticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PrintJobStatisticsDto>> GetJobStatisticsAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            PrintJobStatisticsDto? stats = await predictionService.GetJobStatisticsAsync(jobId, cancellationToken);
            return stats == null ? NotFound($"Statistics for job {jobId} not found") : Ok(stats);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get duration statistics by material type
    /// </summary>
    /// <param name="material">Material type to filter (optional)</param>
    /// <param name="printerId">Printer ID to filter (optional)</param>
    /// <param name="minSampleSize">Minimum number of samples required</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of material statistics</returns>
    [HttpGet("stats/by-material")]
    [ProducesResponseType(typeof(Dictionary<string, PredictionDurationStatsDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<Dictionary<string, PredictionDurationStatsDto>>> GetMaterialStatsAsync(
        [FromQuery] string? material = null,
        [FromQuery] Guid? printerId = null,
        [FromQuery] int minSampleSize = 3,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Dictionary<string, PredictionDurationStatsDto> stats = await predictionService.GetMaterialStatsAsync(printerId, cancellationToken);
            return Ok(stats);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get duration statistics for a specific printer model
    /// </summary>
    /// <param name="modelId">Printer model ID to filter</param>
    /// <param name="material">Material type to filter (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Duration statistics for the model</returns>
    [HttpGet("stats/model/{modelId:guid}")]
    [ProducesResponseType(typeof(PredictionDurationStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PredictionDurationStatsDto>> GetModelStatsAsync(
        Guid modelId,
        [FromQuery] string? material = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            PredictionDurationStatsDto? stats = await predictionService.GetDurationStatsAsync(
                modelId: modelId,
                material: material,
                minSampleSize: 3,
                cancellationToken: cancellationToken);

            return stats == null ? NotFound($"Insufficient data for model {modelId}") : Ok(stats);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Record a job completion for learning (admin only)
    /// </summary>
    /// <param name="jobId">The job ID to record completion for</param>
    /// <param name="request">Completion record request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success response</returns>
    [Authorize(Roles = "farm_admin")]
    [HttpPost("jobs/{jobId:guid}/record-completion")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RecordCompletionAsync(
        Guid jobId,
        [FromBody] RecordCompletionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.ActualDurationMs <= 0)
            {
                return BadRequest(new { error = "ActualDurationMs must be greater than 0" });
            }

            await predictionService.RecordCompletionByJobIdAsync(
                jobId,
                request.ActualDurationMs,
                request.IsSuccess,
                request.FailureReason,
                cancellationToken);

            return Ok(new { message = "Completion recorded successfully" });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

/// <summary>
/// Request model for recording job completion
/// </summary>
public class RecordCompletionRequest
{
    /// <summary>Actual duration of the print in milliseconds</summary>
    public required long ActualDurationMs { get; set; }

    /// <summary>Whether the job completed successfully</summary>
    public bool IsSuccess { get; set; } = true;  // Has default value, not required

    /// <summary>Reason for failure if not successful</summary>
    public string? FailureReason { get; set; }
}
